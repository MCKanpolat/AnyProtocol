using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using AnyProtocol.Abstraction;

namespace AnyProtocol.Internals;

internal sealed class ContractProxyBuilder
{
    private const MethodAttributes ContractMethodAttributes =
        MethodAttributes.Public |
        MethodAttributes.Final |
        MethodAttributes.Virtual |
        MethodAttributes.HideBySig |
        MethodAttributes.NewSlot;

    private readonly ConcurrentDictionary<Type, Lazy<Type>> _proxyTypes = new();
    private readonly ModuleBuilder _module;

    public ContractProxyBuilder()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("AnyProtocol.DynamicProxies"),
            AssemblyBuilderAccess.RunAndCollect);
        _module = assembly.DefineDynamicModule("AnyProtocol.DynamicProxies");
    }

    public Type GetOrCreateProxyType(ContractDescriptor descriptor)
        => _proxyTypes.GetOrAdd(
                descriptor.ContractType,
                _ => new Lazy<Type>(() => BuildType(descriptor), LazyThreadSafetyMode.ExecutionAndPublication))
            .Value;

    private Type BuildType(ContractDescriptor descriptor)
    {
        var contractType = descriptor.ContractType;
        var safeName = (contractType.FullName ?? contractType.Name)
            .Replace('+', '.')
            .Replace('`', '_');
        var typeBuilder = _module.DefineType(
            $"AnyProtocol.DynamicProxies.{safeName}Proxy",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
        typeBuilder.AddInterfaceImplementation(contractType);

        var invokerField = typeBuilder.DefineField(
            "_invoker",
            typeof(IClientInvoker),
            FieldAttributes.Private | FieldAttributes.InitOnly);
        var descriptorsField = typeBuilder.DefineField(
            "_methods",
            typeof(ContractMethodDescriptor[]),
            FieldAttributes.Private | FieldAttributes.InitOnly);

        DefineConstructor(typeBuilder, invokerField, descriptorsField);
        for (var index = 0; index < descriptor.Methods.Count; index++)
        {
            DefineMethod(
                typeBuilder,
                descriptor.Methods[index],
                index,
                invokerField,
                descriptorsField);
        }

        return typeBuilder.CreateTypeInfo()!.AsType();
    }

    private static void DefineConstructor(
        TypeBuilder typeBuilder,
        FieldBuilder invokerField,
        FieldBuilder descriptorsField)
    {
        var constructor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [typeof(IClientInvoker), typeof(ContractMethodDescriptor[])]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, invokerField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Stfld, descriptorsField);
        il.Emit(OpCodes.Ret);
    }

    private static void DefineMethod(
        TypeBuilder typeBuilder,
        ContractMethodDescriptor descriptor,
        int descriptorIndex,
        FieldBuilder invokerField,
        FieldBuilder descriptorsField)
    {
        var contractMethod = descriptor.Method;
        var parameters = contractMethod.GetParameters();
        var methodBuilder = typeBuilder.DefineMethod(
            contractMethod.Name,
            ContractMethodAttributes,
            contractMethod.ReturnType,
            parameters.Select(parameter => parameter.ParameterType).ToArray());

        for (var index = 0; index < parameters.Length; index++)
        {
            methodBuilder.DefineParameter(index + 1, parameters[index].Attributes, parameters[index].Name);
        }

        var il = methodBuilder.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, invokerField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, descriptorsField);
        EmitLoadInt(il, descriptorIndex);
        il.Emit(OpCodes.Ldelem_Ref);

        var payloadIndex = Array.FindIndex(
            parameters,
            parameter => parameter.ParameterType != typeof(CancellationToken));
        if (payloadIndex >= 0)
        {
            EmitLoadArgument(il, payloadIndex + 1);
        }
        else
        {
            il.Emit(OpCodes.Call, typeof(EmptyRequest).GetProperty(nameof(EmptyRequest.Instance))!.GetMethod!);
        }

        var cancellationIndex = Array.FindIndex(
            parameters,
            parameter => parameter.ParameterType == typeof(CancellationToken));
        if (cancellationIndex >= 0)
        {
            EmitLoadArgument(il, cancellationIndex + 1);
        }
        else
        {
            var cancellation = il.DeclareLocal(typeof(CancellationToken));
            il.Emit(OpCodes.Ldloca_S, cancellation);
            il.Emit(OpCodes.Initobj, typeof(CancellationToken));
            il.Emit(OpCodes.Ldloc, cancellation);
        }

        var requestType = descriptor.RequestType;
        switch (descriptor.Operation)
        {
            case ContractOperation.Request:
                EmitRequestCall(il, contractMethod.ReturnType, requestType, descriptor.ResponseType!);
                break;
            case ContractOperation.Send:
                EmitSendCall(il, contractMethod.ReturnType, requestType);
                break;
            case ContractOperation.Stream:
                var streamMethod = GetInvokerMethod(nameof(IClientInvoker.StreamAsync), 2)
                    .MakeGenericMethod(requestType, descriptor.ResponseType!);
                il.Emit(OpCodes.Callvirt, streamMethod);
                break;
            default:
                throw new InvalidOperationException($"Unsupported operation '{descriptor.Operation}'.");
        }

        il.Emit(OpCodes.Ret);
        typeBuilder.DefineMethodOverride(methodBuilder, contractMethod);
    }

    private static void EmitRequestCall(
        ILGenerator il,
        Type contractReturnType,
        Type requestType,
        Type responseType)
    {
        var requestMethod = GetInvokerMethod(nameof(IClientInvoker.RequestAsync), 2)
            .MakeGenericMethod(requestType, responseType);
        il.Emit(OpCodes.Callvirt, requestMethod);

        if (contractReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueTaskType = typeof(ValueTask<>).MakeGenericType(responseType);
            var result = il.DeclareLocal(valueTaskType);
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, valueTaskType.GetMethod(nameof(ValueTask<int>.AsTask))!);
        }
    }

    private static void EmitSendCall(ILGenerator il, Type contractReturnType, Type requestType)
    {
        var sendMethod = GetInvokerMethod(nameof(IClientInvoker.SendAsync), 1)
            .MakeGenericMethod(requestType);
        il.Emit(OpCodes.Callvirt, sendMethod);

        if (contractReturnType == typeof(Task))
        {
            var result = il.DeclareLocal(typeof(ValueTask));
            il.Emit(OpCodes.Stloc, result);
            il.Emit(OpCodes.Ldloca, result);
            il.Emit(OpCodes.Call, typeof(ValueTask).GetMethod(nameof(ValueTask.AsTask))!);
        }
    }

    private static MethodInfo GetInvokerMethod(string name, int genericArgumentCount)
        => typeof(IClientInvoker).GetMethods()
            .Single(method => method.Name == name &&
                              method.GetGenericArguments().Length == genericArgumentCount);

    private static void EmitLoadArgument(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); break;
            case 1: il.Emit(OpCodes.Ldarg_1); break;
            case 2: il.Emit(OpCodes.Ldarg_2); break;
            case 3: il.Emit(OpCodes.Ldarg_3); break;
            default: il.Emit(OpCodes.Ldarg, index); break;
        }
    }

    private static void EmitLoadInt(ILGenerator il, int value)
    {
        if (value is >= 0 and <= 8)
        {
            il.Emit(
                value switch
                {
                    0 => OpCodes.Ldc_I4_0,
                    1 => OpCodes.Ldc_I4_1,
                    2 => OpCodes.Ldc_I4_2,
                    3 => OpCodes.Ldc_I4_3,
                    4 => OpCodes.Ldc_I4_4,
                    5 => OpCodes.Ldc_I4_5,
                    6 => OpCodes.Ldc_I4_6,
                    7 => OpCodes.Ldc_I4_7,
                    _ => OpCodes.Ldc_I4_8
                });
            return;
        }

        il.Emit(OpCodes.Ldc_I4, value);
    }
}
