using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AnyProtocol.Generator;

/// <summary>
/// Provides the anyprotocol generator implementation used by AnyProtocol applications.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class AnyProtocolGenerator : IIncrementalGenerator
{
    private const string LinkBuilderType = "AnyProtocol.Configuration.LinkBuilder";
    private const string ChannelAttribute = "AnyProtocol.Abstraction.ChannelAttribute";
    private const string ExpectReplyAttribute = "AnyProtocol.Abstraction.ExpectReplyAttribute";
    private const string IdempotentAttribute = "AnyProtocol.Abstraction.IdempotentAttribute";
    private const string McpToolAttribute = "AnyProtocol.Abstraction.McpToolAttribute";
    private const string FaultContractAttribute = "AnyProtocol.Abstraction.FaultContractAttribute";
    private const string PartitionKeyAttribute = "AnyProtocol.Abstraction.PartitionKeyAttribute";
    private const string RequirePermissionAttribute = "AnyProtocol.Abstraction.RequirePermissionAttribute";
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
                SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
    private static readonly SymbolDisplayFormat RuntimeTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithMiscellaneousOptions(
                SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly DiagnosticDescriptor InterfaceRequired = new(
        "CLNK001",
        "AnyProtocol contracts must be interfaces",
        "Contract '{0}' must be an interface",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidMethod = new(
        "CLNK002",
        "AnyProtocol contract method is unsupported",
        "Method '{0}' is unsupported: {1}",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidPayload = new(
        "CLNK003",
        "AnyProtocol payload shape is invalid",
        "Method '{0}' is unsupported: {1}",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCancellation = new(
        "CLNK004",
        "AnyProtocol CancellationToken position is invalid",
        "Method '{0}' is unsupported: CancellationToken must be the final parameter and appear once",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidReturn = new(
        "CLNK005",
        "AnyProtocol return type is invalid",
        "Method '{0}' must return Task, ValueTask, Task<T>, ValueTask<T>, or IAsyncEnumerable<T>",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidPartitionKey = new(
        "CLNK006",
        "AnyProtocol partition key is invalid",
        "Request type '{0}' must contain at most one readable, non-indexed [PartitionKey] property",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UndiscoverableRegistration = new(
        "CLNK007",
        "AnyProtocol registration cannot be source generated",
        "Registration for '{0}' cannot be source generated: {1}",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor UnsupportedMember = new(
        "CLNK008",
        "AnyProtocol contract member is unsupported",
        "Contract member '{0}' is unsupported: {1}",
        "AnyProtocol",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Performs the initialize operation.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var registrations = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) =>
                    node is InvocationExpressionSyntax
                    {
                        Expression: MemberAccessExpressionSyntax
                        {
                            Name: GenericNameSyntax
                            {
                                Identifier.ValueText: "AddClient" or "AddServer"
                            }
                        }
                    },
                static (syntaxContext, _) => FindRegistration(syntaxContext))
            .Where(static registration => registration is not null)
            .Select(static (registration, _) => registration!)
            .Collect();

        context.RegisterSourceOutput(
            registrations,
            static (productionContext, discovered) =>
            {
                var contracts = new List<INamedTypeSymbol>();
                foreach (var registration in discovered)
                {
                    if (registration.Contract is null ||
                        ContainsTypeParameter(registration.RegisteredType))
                    {
                        productionContext.ReportDiagnostic(
                            Diagnostic.Create(
                                UndiscoverableRegistration,
                                registration.Location,
                                registration.RegisteredType.ToDisplayString(),
                                "the contract type must be closed; register each closed contract " +
                                "directly with AddClient<TContract>() or " +
                                "AddServer<TContract, TImplementation>()"));
                        continue;
                    }

                    contracts.Add(registration.Contract);
                }

                foreach (var contract in Distinct(contracts))
                {
                    Generate(productionContext, contract);
                }
            });
    }

    private static RegistrationCandidate? FindRegistration(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
            method.Name is not ("AddClient" or "AddServer") ||
            method.TypeArguments.Length < 1 ||
            method.ContainingType.ToDisplayString() != LinkBuilderType)
        {
            return null;
        }

        var registeredType = method.TypeArguments[0];
        return new RegistrationCandidate(
            registeredType as INamedTypeSymbol,
            registeredType,
            invocation.GetLocation());
    }

    private static IEnumerable<INamedTypeSymbol> Distinct(
        IEnumerable<INamedTypeSymbol> contracts)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in contracts)
        {
            if (seen.Add(contract))
            {
                yield return contract;
            }
        }
    }

    private static void Generate(SourceProductionContext context, INamedTypeSymbol contract)
    {
        var location = contract.Locations.FirstOrDefault();
        if (contract.TypeKind != TypeKind.Interface)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InterfaceRequired,
                    location,
                    contract.ToDisplayString()));
            return;
        }

        var hierarchy = contract.AllInterfaces
            .Reverse()
            .Append(contract)
            .ToArray();
        var unsupportedMembers = hierarchy
            .SelectMany(static type => type.GetMembers())
            .Where(
                static member =>
                    member.DeclaredAccessibility == Accessibility.Public &&
                    (member is IPropertySymbol or IEventSymbol ||
                     member is IMethodSymbol method &&
                     IsUnsupportedStaticMethod(method)))
            .ToArray();
        if (unsupportedMembers.Length > 0)
        {
            foreach (var member in unsupportedMembers)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        UnsupportedMember,
                        member.Locations.FirstOrDefault(),
                        member.Name,
                        member.IsStatic
                            ? "static contract methods are not supported"
                            : "properties and events are not supported"));
            }

            return;
        }

        var methods = hierarchy
            .SelectMany(static type => type.GetMembers().OfType<IMethodSymbol>())
            .Where(
                static method =>
                    method.MethodKind == MethodKind.Ordinary &&
                    !method.IsStatic &&
                    method.DeclaredAccessibility == Accessibility.Public)
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .Select(static group => group.ToArray())
            .ToArray();
        var valid = true;
        foreach (var overloads in methods)
        {
            if (overloads.Length > 1)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidMethod,
                        overloads[0].Locations.FirstOrDefault(),
                        overloads[0].Name,
                        "overloaded wire method names are not allowed"));
                valid = false;
                continue;
            }

            valid &= ValidateMethod(context, overloads[0]);
        }

        if (!valid)
        {
            return;
        }

        var source = RenderProxy(contract, methods.Select(static group => group[0]).ToArray());
        context.AddSource(GetHintName(contract), SourceText.From(source, Encoding.UTF8));
    }

    private static bool ValidateMethod(
        SourceProductionContext context,
        IMethodSymbol method)
    {
        var valid = true;
        if (method.IsGenericMethod || method.Parameters.Any(static parameter => parameter.RefKind != RefKind.None))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidMethod,
                    method.Locations.FirstOrDefault(),
                    method.Name,
                    "generic methods and ref/out/in parameters are not allowed"));
            valid = false;
        }

        var cancellationParameters = method.Parameters
            .Where(static parameter => IsCancellationToken(parameter.Type))
            .ToArray();
        if (cancellationParameters.Length > 1 ||
            cancellationParameters.Length == 1 &&
            !SymbolEqualityComparer.Default.Equals(cancellationParameters[0], method.Parameters.Last()))
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidCancellation,
                    method.Locations.FirstOrDefault(),
                    method.Name));
            valid = false;
        }

        var payloads = method.Parameters
            .Where(static parameter => !IsCancellationToken(parameter.Type))
            .ToArray();
        if (payloads.Length > 1 || payloads.Length == 1 && payloads[0].Type.IsValueType)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidPayload,
                    method.Locations.FirstOrDefault(),
                    method.Name,
                    "use zero or one reference-type payload plus an optional CancellationToken"));
            valid = false;
        }
        else if (payloads.Length == 1 &&
                 payloads[0].Type is INamedTypeSymbol payloadType)
        {
            var partitionKeys = GetPartitionKeyProperties(payloadType);
            if (partitionKeys.Length > 1 ||
                partitionKeys.Length == 1 &&
                (partitionKeys[0].GetMethod is null || partitionKeys[0].IsIndexer))
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        InvalidPartitionKey,
                        payloads[0].Locations.FirstOrDefault(),
                        payloadType.ToDisplayString()));
                valid = false;
            }
        }

        if (GetOperation(method.ReturnType) == Operation.Invalid)
        {
            context.ReportDiagnostic(
                Diagnostic.Create(
                    InvalidReturn,
                    method.Locations.FirstOrDefault(),
                    method.Name));
            valid = false;
        }

        return valid;
    }

    private static string RenderProxy(
        INamedTypeSymbol contract,
        IReadOnlyList<IMethodSymbol> methods)
    {
        var contractName = contract.ToDisplayString(TypeFormat);
        var hash = GetStableHash(contract.ToDisplayString());
        var proxyName = $"AnyProtocolProxy_{Sanitize(contract.Name)}_{hash}";
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace AnyProtocol.Generated");
        builder.AppendLine("{");
        builder.Append("    internal sealed class ").Append(proxyName)
            .Append(" : ").Append(contractName).AppendLine();
        builder.AppendLine("    {");
        builder.AppendLine("        private readonly global::AnyProtocol.Abstraction.IClientInvoker _invoker;");
        for (var index = 0; index < methods.Count; index++)
        {
            builder.Append("        private readonly global::AnyProtocol.Abstraction.ContractMethodDescriptor _method")
                .Append(index).AppendLine(";");
        }

        builder.Append("        internal ").Append(proxyName)
            .AppendLine("(global::AnyProtocol.Abstraction.IClientInvoker invoker, global::AnyProtocol.ContractDescriptor descriptor)");
        builder.AppendLine("        {");
        builder.AppendLine("            _invoker = invoker;");
        for (var index = 0; index < methods.Count; index++)
        {
            builder.Append("            _method").Append(index)
                .Append(" = global::System.Linq.Enumerable.Single(descriptor.Methods, static method => method.MethodName == \"")
                .Append(Escape(methods[index].Name)).AppendLine("\");");
        }

        builder.AppendLine("        }");
        for (var index = 0; index < methods.Count; index++)
        {
            RenderMethod(builder, methods[index], index);
        }

        builder.AppendLine("    }");
        builder.AppendLine();
        builder.AppendLine("    internal static class " + proxyName + "_Registration");
        builder.AppendLine("    {");
        builder.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("        internal static void Register()");
        builder.AppendLine("        {");
        builder.Append("            global::AnyProtocol.GeneratedContractRegistry.Register(typeof(")
            .Append(contractName)
            .AppendLine("),");
        builder.AppendLine("                static channelPrefix => CreateDescriptor(channelPrefix),");
        builder.AppendLine("                SerializableTypes,");
        builder.AppendLine("                ServerMethods,");
        builder.Append("                static (invoker, descriptors) => new ")
            .Append(proxyName).Append("(invoker, descriptors.CreateGenerated(typeof(")
            .Append(contractName).AppendLine("))));");
        builder.AppendLine("        }");
        RenderDescriptorFactory(builder, contract, methods);
        RenderServerDispatchers(builder, contract, methods);
        builder.AppendLine("    }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void RenderDescriptorFactory(
        StringBuilder builder,
        INamedTypeSymbol contract,
        IReadOnlyList<IMethodSymbol> methods)
    {
        var contractName = contract.ToDisplayString(TypeFormat);
        var serializableTypes = new HashSet<string>(StringComparer.Ordinal);
        serializableTypes.Add("global::AnyProtocol.Abstraction.FaultMessage");
        serializableTypes.Add("global::AnyProtocol.Abstraction.Unit");
        serializableTypes.Add("global::System.Object");
        foreach (var method in methods)
        {
            var payload = method.Parameters.FirstOrDefault(
                static parameter => !IsCancellationToken(parameter.Type));
            serializableTypes.Add(
                payload?.Type.ToDisplayString(RuntimeTypeFormat) ??
                "global::AnyProtocol.Abstraction.EmptyRequest");

            if (GetResponseType(method.ReturnType) is { } responseType)
            {
                serializableTypes.Add(responseType.ToDisplayString(RuntimeTypeFormat));
            }

            if (GetFaultType(contract, method) is { } faultType)
            {
                serializableTypes.Add(faultType.ToDisplayString(RuntimeTypeFormat));
            }
        }

        builder.AppendLine();
        builder.AppendLine("        private static readonly global::System.Type[] SerializableTypes =");
        builder.AppendLine("        {");
        foreach (var type in serializableTypes.OrderBy(static type => type, StringComparer.Ordinal))
        {
            builder.Append("            typeof(").Append(type)
                .AppendLine("),");
        }

        builder.AppendLine("        };");
        builder.AppendLine();
        builder.AppendLine("        private static global::AnyProtocol.ContractDescriptor CreateDescriptor(string channelPrefix)");
        builder.AppendLine("            => new(");
        builder.Append("                typeof(").Append(contractName).AppendLine("),");
        builder.AppendLine("                new global::AnyProtocol.Abstraction.ContractMethodDescriptor[]");
        builder.AppendLine("                {");
        foreach (var method in methods)
        {
            RenderMethodDescriptor(builder, contract, method);
        }

        builder.AppendLine("                });");
    }

    private static void RenderServerDispatchers(
        StringBuilder builder,
        INamedTypeSymbol contract,
        IReadOnlyList<IMethodSymbol> methods)
    {
        var contractName = contract.ToDisplayString(TypeFormat);
        builder.AppendLine();
        builder.AppendLine(
            "        private static readonly global::AnyProtocol.GeneratedServerMethodRegistration[] ServerMethods =");
        builder.AppendLine("        {");
        for (var index = 0; index < methods.Count; index++)
        {
            builder.Append("            new(\"").Append(Escape(methods[index].Name))
                .Append("\", Dispatch").Append(index).AppendLine("),");
        }

        builder.AppendLine("        };");
        for (var index = 0; index < methods.Count; index++)
        {
            var method = methods[index];
            var operation = GetOperation(method.ReturnType);
            var payload = method.Parameters.FirstOrDefault(
                static parameter => !IsCancellationToken(parameter.Type));
            var arguments = new List<string>(2);
            if (payload is not null)
            {
                arguments.Add(
                    $"({payload.Type.ToDisplayString(TypeFormat)})request!");
            }

            if (method.Parameters.Any(static parameter => IsCancellationToken(parameter.Type)))
            {
                arguments.Add("cancellationToken");
            }

            var invocation = $"(({contractName})target).@{method.Name}(" +
                             string.Join(", ", arguments) + ")";
            builder.AppendLine();
            if (operation == Operation.Stream)
            {
                builder.Append("        private static async global::System.Collections.Generic.IAsyncEnumerable<object?> Dispatch")
                    .Append(index).AppendLine("(");
                builder.AppendLine("            object target,");
                builder.AppendLine("            object? request,");
                builder.AppendLine(
                    "            [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken cancellationToken)");
                builder.AppendLine("        {");
                builder.Append("            await using var enumerator = ").Append(invocation)
                    .AppendLine(".GetAsyncEnumerator(cancellationToken);");
                builder.AppendLine(
                    "            while (await enumerator.MoveNextAsync().ConfigureAwait(false))");
                builder.AppendLine("            {");
                builder.AppendLine("                yield return enumerator.Current;");
                builder.AppendLine("            }");
                builder.AppendLine("        }");
                continue;
            }

            builder.Append("        private static async global::System.Threading.Tasks.ValueTask<object?> Dispatch")
                .Append(index).AppendLine("(");
            builder.AppendLine("            object target,");
            builder.AppendLine("            object? request,");
            builder.AppendLine(
                "            global::System.Threading.CancellationToken cancellationToken)");
            builder.AppendLine("        {");
            if (operation is Operation.SendTask or Operation.SendValueTask)
            {
                builder.Append("            await ").Append(invocation)
                    .AppendLine(".ConfigureAwait(false);");
                builder.AppendLine("            return null;");
            }
            else
            {
                builder.Append("            return await ").Append(invocation)
                    .AppendLine(".ConfigureAwait(false);");
            }

            builder.AppendLine("        }");
        }
    }

    private static void RenderMethodDescriptor(
        StringBuilder builder,
        INamedTypeSymbol contract,
        IMethodSymbol method)
    {
        var contractName = contract.ToDisplayString(TypeFormat);
        var declaringType = method.ContainingType.ToDisplayString(TypeFormat);
        var payload = method.Parameters.FirstOrDefault(
            static parameter => !IsCancellationToken(parameter.Type));
        var requestType = payload?.Type;
        var responseType = GetResponseType(method.ReturnType);
        var operation = GetOperation(method.ReturnType);
        var partitionKey = requestType is INamedTypeSymbol namedRequest
            ? GetPartitionKeyProperties(namedRequest).SingleOrDefault()
            : null;
        var permissions = contract.AllInterfaces
            .Reverse()
            .Append(contract)
            .SelectMany(static type => GetStringAttributeValues(type, RequirePermissionAttribute))
            .Concat(GetStringAttributeValues(method, RequirePermissionAttribute))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        builder.AppendLine("                    new()");
        builder.AppendLine("                    {");
        builder.Append("                        ContractType = typeof(").Append(contractName)
            .AppendLine("),");
        builder.Append("                        Method = typeof(").Append(declaringType)
            .Append(").GetMethod(\"").Append(Escape(method.Name)).Append("\", ");
        if (method.Parameters.Length == 0)
        {
            builder.AppendLine("global::System.Type.EmptyTypes)!,");
        }
        else
        {
            builder.Append("new global::System.Type[] { ");
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append("typeof(")
                    .Append(method.Parameters[index].Type.ToDisplayString(RuntimeTypeFormat))
                    .Append(')');
            }

            builder.AppendLine(" })!,");
        }

        builder.Append("                        ContractName = typeof(").Append(contractName)
            .AppendLine(").FullName!,");
        builder.Append("                        MethodName = \"").Append(Escape(method.Name))
            .AppendLine("\",");
        builder.Append("                        Channel = ").Append(GetChannelExpression(contract, method))
            .AppendLine(",");
        builder.Append("                        RequestType = typeof(")
            .Append(requestType?.ToDisplayString(RuntimeTypeFormat) ??
                    "global::AnyProtocol.Abstraction.EmptyRequest")
            .AppendLine("),");
        builder.Append("                        ResponseType = ")
            .Append(responseType is null
                ? "null"
                : $"typeof({responseType.ToDisplayString(RuntimeTypeFormat)})")
            .AppendLine(",");
        builder.Append("                        Operation = global::AnyProtocol.Abstraction.ContractOperation.")
            .Append(GetOperationName(operation)).AppendLine(",");
        builder.Append("                        HasCancellationToken = ")
            .Append(method.Parameters.Any(static parameter => IsCancellationToken(parameter.Type))
                ? "true"
                : "false")
            .AppendLine(",");
        builder.Append("                        ExpectReply = ")
            .Append(operation is Operation.RequestTask or Operation.RequestValueTask ||
                    HasAttribute(method, ExpectReplyAttribute)
                ? "true"
                : "false")
            .AppendLine(",");
        builder.Append("                        IsIdempotent = ")
            .Append(
                HasAttribute(method, IdempotentAttribute) ||
                contract.AllInterfaces.Append(contract)
                    .Any(static type => HasAttribute(type, IdempotentAttribute)) ||
                GetNamedBooleanAttributeValue(method, McpToolAttribute, "Idempotent")
                    ? "true"
                    : "false")
            .AppendLine(",");
        builder.Append("                        RequiredPermissions = ");
        if (permissions.Length == 0)
        {
            builder.AppendLine("global::System.Array.Empty<string>(),");
        }
        else
        {
            builder.Append("new string[] { ")
                .Append(string.Join(
                    ", ",
                    permissions.Select(static value => $"\"{Escape(value)}\"")))
                .AppendLine(" },");
        }

        var faultType = GetFaultType(contract, method);
        builder.Append("                        FaultType = ")
            .Append(faultType is null
                ? "null"
                : $"typeof({faultType.ToDisplayString(RuntimeTypeFormat)})")
            .AppendLine(",");
        builder.Append("                        PartitionKeyProperty = ")
            .Append(partitionKey is null
                ? "null"
                : $"typeof({partitionKey.ContainingType.ToDisplayString(RuntimeTypeFormat)}).GetProperty(\"{Escape(partitionKey.Name)}\")")
            .AppendLine();
        builder.AppendLine("                    },");
    }

    private static void RenderMethod(StringBuilder builder, IMethodSymbol method, int index)
    {
        var returnType = method.ReturnType.ToDisplayString(TypeFormat);
        builder.Append("        public ").Append(returnType).Append(" @")
            .Append(method.Name).Append('(');
        for (var parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
        {
            if (parameterIndex > 0)
            {
                builder.Append(", ");
            }

            var parameter = method.Parameters[parameterIndex];
            builder.Append(parameter.Type.ToDisplayString(TypeFormat))
                .Append(" @").Append(parameter.Name);
            if (parameter.HasExplicitDefaultValue)
            {
                builder.Append(" = default");
            }
        }

        builder.AppendLine(")");
        var payload = method.Parameters.FirstOrDefault(
            static parameter => !IsCancellationToken(parameter.Type));
        var cancellation = method.Parameters.FirstOrDefault(
            static parameter => IsCancellationToken(parameter.Type));
        var requestType = payload?.Type.ToDisplayString(TypeFormat) ??
                          "global::AnyProtocol.Abstraction.EmptyRequest";
        var request = payload is null
            ? "global::AnyProtocol.Abstraction.EmptyRequest.Instance"
            : "@" + payload.Name;
        var token = cancellation is null ? "default" : "@" + cancellation.Name;
        var operation = GetOperation(method.ReturnType);
        builder.AppendLine("        {");
        builder.Append("            return ");
        switch (operation)
        {
            case Operation.SendTask:
                builder.Append("_invoker.SendAsync<").Append(requestType).Append(">(_method")
                    .Append(index).Append(", ").Append(request).Append(", ").Append(token)
                    .AppendLine(").AsTask();");
                break;
            case Operation.SendValueTask:
                builder.Append("_invoker.SendAsync<").Append(requestType).Append(">(_method")
                    .Append(index).Append(", ").Append(request).Append(", ").Append(token)
                    .AppendLine(");");
                break;
            case Operation.RequestTask:
            case Operation.RequestValueTask:
                var responseType = ((INamedTypeSymbol)method.ReturnType)
                    .TypeArguments[0].ToDisplayString(TypeFormat);
                builder.Append("_invoker.RequestAsync<").Append(requestType).Append(", ")
                    .Append(responseType).Append(">(_method").Append(index).Append(", ")
                    .Append(request).Append(", ").Append(token).Append(')');
                builder.AppendLine(operation == Operation.RequestTask ? ".AsTask();" : ";");
                break;
            case Operation.Stream:
                var itemType = ((INamedTypeSymbol)method.ReturnType)
                    .TypeArguments[0].ToDisplayString(TypeFormat);
                builder.Append("_invoker.StreamAsync<").Append(requestType).Append(", ")
                    .Append(itemType).Append(">(_method").Append(index).Append(", ")
                    .Append(request).Append(", ").Append(token).AppendLine(");");
                break;
        }

        builder.AppendLine("        }");
    }

    private static Operation GetOperation(ITypeSymbol returnType)
    {
        var name = returnType.OriginalDefinition.ToDisplayString();
        return name switch
        {
            "System.Threading.Tasks.Task" => Operation.SendTask,
            "System.Threading.Tasks.ValueTask" => Operation.SendValueTask,
            "System.Threading.Tasks.Task<TResult>" => Operation.RequestTask,
            "System.Threading.Tasks.ValueTask<TResult>" => Operation.RequestValueTask,
            "System.Collections.Generic.IAsyncEnumerable<T>" => Operation.Stream,
            _ => Operation.Invalid
        };
    }

    private static ITypeSymbol? GetResponseType(ITypeSymbol returnType)
        => GetOperation(returnType) is
            Operation.RequestTask or
            Operation.RequestValueTask or
            Operation.Stream
            ? ((INamedTypeSymbol)returnType).TypeArguments[0]
            : null;

    private static string GetOperationName(Operation operation)
        => operation switch
        {
            Operation.SendTask or Operation.SendValueTask => "Send",
            Operation.RequestTask or Operation.RequestValueTask => "Request",
            Operation.Stream => "Stream",
            _ => throw new InvalidOperationException("Invalid contract operation.")
        };

    private static string GetChannelExpression(
        INamedTypeSymbol contract,
        IMethodSymbol method)
    {
        var methodRoute = method.Name.EndsWith("Async", StringComparison.Ordinal)
            ? method.Name.Substring(0, method.Name.Length - 5)
            : method.Name;
        if (GetStringAttributeValue(method, ChannelAttribute) is { } methodChannel &&
            !string.IsNullOrWhiteSpace(methodChannel))
        {
            return $"\"{Escape(methodChannel)}\"";
        }

        if (GetStringAttributeValue(contract, ChannelAttribute) is { } contractChannel &&
            !string.IsNullOrWhiteSpace(contractChannel))
        {
            return $"\"{Escape(contractChannel.Trim('.'))}.{ToKebabCase(methodRoute)}\"";
        }

        var contractRoute = contract.Name;
        if (contractRoute.Length > 1 &&
            contractRoute[0] == 'I' &&
            char.IsUpper(contractRoute[1]))
        {
            contractRoute = contractRoute.Substring(1);
        }

        return $"channelPrefix + \".{ToKebabCase(contractRoute)}.{ToKebabCase(methodRoute)}\"";
    }

    private static ITypeSymbol? GetFaultType(
        INamedTypeSymbol contract,
        IMethodSymbol method)
        => GetTypeAttributeValue(method, FaultContractAttribute) ??
           GetTypeAttributeValue(method.ContainingType, FaultContractAttribute) ??
           GetTypeAttributeValue(contract, FaultContractAttribute);

    private static bool HasAttribute(ISymbol symbol, string metadataName)
        => GetAttribute(symbol, metadataName) is not null;

    private static AttributeData? GetAttribute(ISymbol symbol, string metadataName)
        => symbol.GetAttributes().FirstOrDefault(
            attribute => attribute.AttributeClass?.ToDisplayString() == metadataName);

    private static string? GetStringAttributeValue(ISymbol symbol, string metadataName)
        => GetAttribute(symbol, metadataName)?.ConstructorArguments.FirstOrDefault().Value as string;

    private static IEnumerable<string> GetStringAttributeValues(
        ISymbol symbol,
        string metadataName)
        => symbol.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == metadataName)
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value as string)
            .Where(static value => value is not null)!;

    private static ITypeSymbol? GetTypeAttributeValue(ISymbol symbol, string metadataName)
        => GetAttribute(symbol, metadataName)?.ConstructorArguments.FirstOrDefault().Value
            as ITypeSymbol;

    private static bool GetNamedBooleanAttributeValue(
        ISymbol symbol,
        string metadataName,
        string argumentName)
        => GetAttribute(symbol, metadataName)?.NamedArguments
            .FirstOrDefault(argument => argument.Key == argumentName)
            .Value.Value as bool? == true;

    private static bool IsCancellationToken(ITypeSymbol type)
        => type.ToDisplayString() == "System.Threading.CancellationToken";

    private static bool IsUnsupportedStaticMethod(IMethodSymbol method)
        => method.IsStatic &&
           method.MethodKind is not (
               MethodKind.PropertyGet or
               MethodKind.PropertySet or
               MethodKind.EventAdd or
               MethodKind.EventRemove);

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol ||
           type is INamedTypeSymbol namedType &&
           (namedType.IsUnboundGenericType ||
            namedType.TypeArguments.Any(ContainsTypeParameter) ||
            namedType.ContainingType is not null &&
            ContainsTypeParameter(namedType.ContainingType));

    private static IPropertySymbol[] GetPartitionKeyProperties(INamedTypeSymbol type)
    {
        var properties = new List<IPropertySymbol>();
        for (var current = type; current is not null; current = current.BaseType)
        {
            properties.AddRange(
                current.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(static property => HasAttribute(property, PartitionKeyAttribute)));
        }

        properties.AddRange(
            type.AllInterfaces
                .SelectMany(static item => item.GetMembers().OfType<IPropertySymbol>())
                .Where(static property => HasAttribute(property, PartitionKeyAttribute)));
        return properties
            .Distinct<IPropertySymbol>(SymbolEqualityComparer.Default)
            .ToArray();
    }

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsUpper(current) &&
                index > 0 &&
                (!char.IsUpper(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }

    private static string GetHintName(INamedTypeSymbol contract)
        => $"{Sanitize(contract.ToDisplayString())}.{GetStableHash(contract.ToDisplayString())}.g.cs";

    private static string GetStableHash(string value)
    {
        using var algorithm = SHA256.Create();
        var bytes = algorithm.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(8);
        for (var index = 0; index < 4; index++)
        {
            builder.Append(bytes[index].ToString("x2"));
        }

        return builder.ToString();
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    private static string Escape(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private enum Operation
    {
        Invalid,
        SendTask,
        SendValueTask,
        RequestTask,
        RequestValueTask,
        Stream
    }

    private sealed class RegistrationCandidate
    {
        public RegistrationCandidate(
            INamedTypeSymbol? contract,
            ITypeSymbol registeredType,
            Location location)
        {
            Contract = contract;
            RegisteredType = registeredType;
            Location = location;
        }

        public INamedTypeSymbol? Contract { get; }

        public ITypeSymbol RegisteredType { get; }

        public Location Location { get; }
    }
}
