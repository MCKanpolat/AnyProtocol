using System.Collections.Frozen;
using System.Collections.Immutable;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Configuration;

internal sealed class RuntimePlanCompiler(ContractDescriptorFactory descriptorFactory)
{
    public RuntimePlan Compile(LinkDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate(descriptorFactory);
        RejectDuplicateClientContracts(definition.Clients);
        RejectDuplicateEventRoutes(definition.Events);

        var transports = definition.Transports.ToFrozenDictionary();
        var clients = definition.Clients.Select(Snapshot).ToImmutableArray();
        var servers = definition.Servers.Select(Snapshot).ToImmutableArray();
        var events = definition.Events.Select(Snapshot).ToImmutableArray();

        var clientPlans = clients
            .Select(registration => new ClientPlan(
                registration,
                descriptorFactory.CreateRuntimeCompatible(registration.ContractType)))
            .ToFrozenDictionary(static plan => plan.Registration.ContractType);
        var serverRoutes = servers
            .SelectMany(
                registration => registration.Protocols.SelectMany(
                    protocol => descriptorFactory.CreateRuntimeCompatible(registration.ContractType).Methods
                        .Select(method => new ServerRoutePlan(registration, method, protocol))))
            .ToImmutableArray();
        var eventPlans = events.Select(static registration => new EventPlan(registration)).ToImmutableArray();
        var mcpToolPlans = serverRoutes
            .Where(static route => route.Protocol == ProtocolKey.Mcp)
            .Select(
                static route => (
                    Route: route,
                    Metadata: route.Method.Method.GetCustomAttributes(typeof(McpToolAttribute), false)
                        .Cast<McpToolAttribute>()
                        .SingleOrDefault()))
            .Where(static candidate => candidate.Metadata is not null)
            .Select(
                static candidate => new McpToolPlan(
                    string.IsNullOrWhiteSpace(candidate.Metadata!.Name)
                        ? GetDefaultMcpToolName(
                            candidate.Route.Registration.ContractType,
                            candidate.Route.Method.MethodName)
                        : candidate.Metadata.Name!,
                    candidate.Metadata.Description,
                    candidate.Metadata.ReadOnly,
                    candidate.Metadata.Destructive,
                    candidate.Metadata.OpenWorld,
                    candidate.Route.Registration,
                    candidate.Route.Method))
            .ToImmutableArray();

        return new RuntimePlan(
            definition.Serializer!,
            transports,
            clients,
            servers,
            events,
            definition.ClientFilters.ToImmutableArray(),
            definition.ServerFilters.ToImmutableArray(),
            clientPlans,
            serverRoutes,
            serverRoutes
                .GroupBy(static route => route.Protocol)
                .ToFrozenDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray()),
            eventPlans,
            eventPlans
                .GroupBy(static plan => plan.Registration.Protocol)
                .ToFrozenDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray()),
            mcpToolPlans,
            definition.LargePayloadOffload is null
                ? null
                : new LargePayloadOffloadPolicy(
                    definition.LargePayloadOffload.StoreName!,
                    definition.LargePayloadOffload.MaxInlinePayloadBytes,
                    definition.LargePayloadOffload.MaxStoredPayloadBytes,
                    definition.LargePayloadOffload.TimeToLive));
    }

    private static ClientRegistration Snapshot(ClientRegistration registration)
        => new(
            registration.ContractType,
            registration.Protocol,
            registration.Timeout,
            registration.MaxRetryAttempts)
        {
            ConfigurationKey = registration.ConfigurationKey,
            RequiredOrdering = registration.RequiredOrdering
        };

    private static ServerRegistration Snapshot(ServerRegistration registration)
        => new(
            registration.ContractType,
            registration.ImplementationType,
            registration.Protocols.ToImmutableArray())
        {
            ConfigurationKey = registration.ConfigurationKey,
            RequiredOrdering = registration.RequiredOrdering
        };

    private static EventRegistration Snapshot(EventRegistration registration)
        => new(
            registration.EventType,
            registration.HandlerType,
            registration.Protocol,
            registration.Channel,
            registration.ConsumerGroup)
        {
            RequiredOrdering = registration.RequiredOrdering
        };

    private static void RejectDuplicateClientContracts(IEnumerable<ClientRegistration> registrations)
    {
        var duplicate = registrations
            .GroupBy(static registration => registration.ContractType)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Client contract '{duplicate.Key.FullName}' is registered more than once. " +
                "Register each client contract exactly once.");
        }
    }

    private static void RejectDuplicateEventRoutes(IEnumerable<EventRegistration> registrations)
    {
        var duplicate = registrations
            .GroupBy(static registration => (
                registration.Protocol,
                registration.Channel,
                registration.EventType))
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Event '{duplicate.Key.EventType.FullName}' is registered more than once " +
                $"for protocol '{duplicate.Key.Protocol}' and channel '{duplicate.Key.Channel}'.");
        }
    }

    private static string GetDefaultMcpToolName(Type contractType, string methodName)
    {
        var contractName = contractType.Name;
        if (contractName.Length > 1 &&
            contractName[0] == 'I' &&
            char.IsUpper(contractName[1]))
        {
            contractName = contractName[1..];
        }

        if (methodName.EndsWith("Async", StringComparison.Ordinal))
        {
            methodName = methodName[..^5];
        }

        return $"{ToSnakeCase(contractName)}_{ToSnakeCase(methodName)}";
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) &&
                index > 0 &&
                (!char.IsUpper(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
