using System.Collections.Concurrent;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Creates contract descriptor instances.
/// </summary>
public sealed class ContractDescriptorFactory
{
    private readonly ConcurrentDictionary<Type, ContractDescriptor> _cache = new();
    private readonly string _channelPrefix;

    /// <summary>
    /// Initializes a new instance of the ContractDescriptorFactory class.
    /// </summary>
    /// <param name="channelPrefix">The channel prefix.</param>
    public ContractDescriptorFactory(string channelPrefix = "anyprotocol")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelPrefix);
        _channelPrefix = channelPrefix.Trim('.');
    }

    /// <summary>
    /// Creates &lt;t contract&gt;.
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <returns>The result of the create&lt;t contract&gt; operation.</returns>
    [RequiresDynamicCode("Runtime contract discovery uses reflection. Use source-generated descriptors for Native AOT.")]
    [RequiresUnreferencedCode("Runtime contract discovery requires contract method metadata.")]
    public ContractDescriptor Create<TContract>() => CreateCore(typeof(TContract));

    /// <summary>
    /// Creates .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>The result of the create operation.</returns>
    [RequiresDynamicCode("Runtime contract discovery uses reflection. Use source-generated descriptors for Native AOT.")]
    [RequiresUnreferencedCode("Runtime contract discovery requires contract method metadata.")]
    public ContractDescriptor Create(Type contractType)
        => CreateCore(contractType);

    /// <summary>
    /// Creates a contract descriptor while using generated metadata when runtime code generation is unavailable.
    /// </summary>
    /// <param name="contractType">The contract interface to describe.</param>
    /// <returns>The descriptor for the requested contract.</returns>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Generated metadata is preferred and reflection is unreachable when dynamic code is disabled.")]
    public ContractDescriptor CreateRuntimeCompatible(Type contractType)
        => CreateCore(contractType);

    private ContractDescriptor CreateCore(Type contractType)
        => _cache.GetOrAdd(
            contractType,
            type => GeneratedContractDescriptorRegistry.TryCreate(
                type,
                _channelPrefix,
                out var generated)
                ? generated!
                : RuntimeFeature.IsDynamicCodeSupported
                    ? Build(type)
                    : throw new InvalidOperationException(
                        $"No generated AnyProtocol descriptor is registered for " +
                        $"'{type.FullName}'. Runtime contract discovery is unavailable because " +
                        "dynamic code is disabled. Reference AnyProtocol.Generator and register " +
                        "the closed contract directly with LinkBuilder.AddClient<TContract>() or " +
                        "AddServer<TContract, TImplementation>()."));

    /// <summary>
    /// Creates generated.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>The result of the create generated operation.</returns>
    public ContractDescriptor CreateGenerated(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return _cache.GetOrAdd(
            contractType,
            type => GeneratedContractDescriptorRegistry.TryCreate(
                type,
                _channelPrefix,
                out var generated)
                ? generated!
                : throw new InvalidOperationException(
                    $"No generated descriptor is registered for '{type.FullName}'."));
    }

    [RequiresUnreferencedCode("Runtime contract discovery requires contract method metadata.")]
    private ContractDescriptor Build(Type contractType)
    {
        if (!contractType.IsInterface)
        {
            throw new ContractShapeException(
                $"Contract '{contractType.FullName}' must be an interface.");
        }

        if (contractType.ContainsGenericParameters)
        {
            throw new ContractShapeException(
                $"Open generic contract '{contractType.FullName}' is not supported.");
        }

        var methods = contractType.GetInterfaces()
            .Append(contractType)
            .SelectMany(type => type.GetMethods())
            .DistinctBy(method => (method.Module, method.MetadataToken))
            .ToArray();
        var unsupportedMethod = methods.FirstOrDefault(
            static method => method.IsStatic || method.IsSpecialName);
        if (unsupportedMethod is not null)
        {
            throw ShapeError(
                contractType,
                unsupportedMethod,
                unsupportedMethod.IsStatic
                    ? "static contract methods are not supported"
                    : "properties and events are not supported");
        }

        var duplicate = methods.GroupBy(method => method.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ContractShapeException(
                $"Contract '{contractType.FullName}' contains overloaded method '{duplicate.Key}'. " +
                "Wire method names must be unique.");
        }

        var descriptors = methods.Select(method => BuildMethod(contractType, method)).ToArray();
        return new ContractDescriptor(contractType, descriptors);
    }

    private ContractMethodDescriptor BuildMethod(Type contractType, MethodInfo method)
    {
        if (method.IsGenericMethodDefinition)
        {
            throw ShapeError(contractType, method, "generic methods are not supported");
        }

        var parameters = method.GetParameters();
        if (parameters.Any(parameter => parameter.ParameterType.IsByRef))
        {
            throw ShapeError(contractType, method, "ref and out parameters are not supported");
        }

        var cancellationIndex = Array.FindIndex(
            parameters,
            parameter => parameter.ParameterType == typeof(CancellationToken));
        if (cancellationIndex >= 0 && cancellationIndex != parameters.Length - 1)
        {
            throw ShapeError(contractType, method, "CancellationToken must be the final parameter");
        }

        if (parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken)) > 1)
        {
            throw ShapeError(contractType, method, "only one CancellationToken is allowed");
        }

        var payloadParameters = parameters
            .Where(parameter => parameter.ParameterType != typeof(CancellationToken))
            .ToArray();
        if (payloadParameters.Length > 1)
        {
            throw ShapeError(
                contractType,
                method,
                "methods may have zero or one payload parameter plus an optional CancellationToken");
        }

        if (payloadParameters is [{ ParameterType.IsValueType: true }])
        {
            throw ShapeError(contractType, method, "payload parameters must be reference types");
        }

        var (operation, responseType) = AnalyzeReturnType(contractType, method);
        var contractName = contractType.FullName ?? contractType.Name;
        var routeContractName = contractType.Name;
        if (routeContractName.Length > 1 &&
            routeContractName[0] == 'I' &&
            char.IsUpper(routeContractName[1]))
        {
            routeContractName = routeContractName[1..];
        }

        var methodRouteName = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name[..^5]
            : method.Name;
        var channel = method.GetCustomAttribute<ChannelAttribute>()?.Name;
        if (string.IsNullOrWhiteSpace(channel))
        {
            var contractChannel = contractType.GetCustomAttribute<ChannelAttribute>()?.Name;
            channel = string.IsNullOrWhiteSpace(contractChannel)
                ? $"{_channelPrefix}.{ToKebabCase(routeContractName)}.{ToKebabCase(methodRouteName)}"
                : $"{contractChannel.Trim('.')}.{ToKebabCase(methodRouteName)}";
        }

        var contractHierarchy = contractType.GetInterfaces().Append(contractType).Distinct();
        var permissions = contractHierarchy
            .SelectMany(type => type.GetCustomAttributes<RequirePermissionAttribute>())
            .Concat(method.GetCustomAttributes<RequirePermissionAttribute>())
            .Select(attribute => attribute.Permission)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var faultType = method.GetCustomAttribute<FaultContractAttribute>()?.FaultType ??
                        method.DeclaringType?.GetCustomAttribute<FaultContractAttribute>()?.FaultType ??
                        contractType.GetCustomAttribute<FaultContractAttribute>()?.FaultType;
        var requestType = payloadParameters.SingleOrDefault()?.ParameterType ?? typeof(EmptyRequest);
        var partitionKeyProperties = requestType.GetInterfaces()
            .Append(requestType)
            .SelectMany(
                static type => type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public))
            .DistinctBy(static property => (property.Module, property.MetadataToken))
            .Where(property => property.IsDefined(typeof(PartitionKeyAttribute)))
            .ToArray();
        if (partitionKeyProperties.Length > 1)
        {
            throw ShapeError(
                contractType,
                method,
                $"request type '{requestType.FullName}' contains more than one [PartitionKey] property");
        }

        var partitionKeyProperty = partitionKeyProperties.SingleOrDefault();
        if (partitionKeyProperty is not null &&
            (!partitionKeyProperty.CanRead || partitionKeyProperty.GetIndexParameters().Length != 0))
        {
            throw ShapeError(
                contractType,
                method,
                $"[PartitionKey] property '{partitionKeyProperty.Name}' must be a readable non-indexed property");
        }

        return new ContractMethodDescriptor
        {
            ContractType = contractType,
            Method = method,
            ContractName = contractName,
            MethodName = method.Name,
            Channel = channel,
            RequestType = requestType,
            ResponseType = responseType,
            Operation = operation,
            HasCancellationToken = cancellationIndex >= 0,
            ExpectReply = method.IsDefined(typeof(ExpectReplyAttribute)) ||
                          operation == ContractOperation.Request,
            IsIdempotent =
                method.IsDefined(typeof(IdempotentAttribute)) ||
                contractHierarchy.Any(type => type.IsDefined(typeof(IdempotentAttribute))) ||
                method.GetCustomAttribute<McpToolAttribute>()?.Idempotent == true,
            RequiredPermissions = permissions,
            FaultType = faultType,
            PartitionKeyProperty = partitionKeyProperty
        };
    }

    private static (ContractOperation Operation, Type? ResponseType) AnalyzeReturnType(
        Type contractType,
        MethodInfo method)
    {
        var returnType = method.ReturnType;
        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return (ContractOperation.Send, null);
        }

        if (returnType.IsGenericType)
        {
            var definition = returnType.GetGenericTypeDefinition();
            var resultType = returnType.GetGenericArguments()[0];
            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                return (ContractOperation.Request, resultType);
            }

            if (definition == typeof(IAsyncEnumerable<>))
            {
                return (ContractOperation.Stream, resultType);
            }
        }

        throw ShapeError(
            contractType,
            method,
            "return type must be Task, ValueTask, Task<T>, ValueTask<T>, or IAsyncEnumerable<T>");
    }

    private static ContractShapeException ShapeError(
        Type contractType,
        MethodInfo method,
        string reason)
        => new($"Invalid contract method '{contractType.FullName}.{method.Name}': {reason}.");

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) && index > 0 &&
                (!char.IsUpper(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
