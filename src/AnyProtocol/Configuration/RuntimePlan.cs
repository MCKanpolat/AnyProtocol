using System.Collections.Frozen;
using System.Collections.Immutable;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol.Configuration;

/// <summary>
/// An immutable, validated description of an AnyProtocol runtime.
/// </summary>
public sealed class RuntimePlan
{
    internal RuntimePlan(
        IMessageSerializer serializer,
        FrozenDictionary<ProtocolKey, IMessagingProtocol> transports,
        ImmutableArray<ClientRegistration> clients,
        ImmutableArray<ServerRegistration> servers,
        ImmutableArray<EventRegistration> events,
        ImmutableArray<IMessageFilter> clientFilters,
        ImmutableArray<IMessageFilter> serverFilters,
        FrozenDictionary<Type, ClientPlan> clientsByContract,
        ImmutableArray<ServerRoutePlan> serverRoutes,
        FrozenDictionary<ProtocolKey, ImmutableArray<ServerRoutePlan>> serverRoutesByProtocol,
        ImmutableArray<EventPlan> eventPlans,
        FrozenDictionary<ProtocolKey, ImmutableArray<EventPlan>> eventPlansByProtocol,
        ImmutableArray<McpToolPlan> mcpToolPlans,
        LargePayloadOffloadPolicy? largePayloadOffload)
    {
        Serializer = serializer;
        RegisteredTransports = transports;
        ClientRegistrations = clients;
        ServerRegistrations = servers;
        EventRegistrations = events;
        RegisteredClientFilters = clientFilters;
        RegisteredServerFilters = serverFilters;
        ClientsByContract = clientsByContract;
        ServerRoutes = serverRoutes;
        ServerRoutesByProtocol = serverRoutesByProtocol;
        EventPlans = eventPlans;
        EventPlansByProtocol = eventPlansByProtocol;
        McpToolPlans = mcpToolPlans;
        LargePayloadOffload = largePayloadOffload;
    }

    /// <summary>Gets the serializer selected during composition.</summary>
    public IMessageSerializer Serializer { get; }

    /// <summary>Gets the immutable transport bindings.</summary>
    public FrozenDictionary<ProtocolKey, IMessagingProtocol> RegisteredTransports { get; }

    /// <summary>Gets immutable client registration snapshots.</summary>
    public ImmutableArray<ClientRegistration> ClientRegistrations { get; }

    /// <summary>Gets immutable server registration snapshots.</summary>
    public ImmutableArray<ServerRegistration> ServerRegistrations { get; }

    /// <summary>Gets immutable event registration snapshots.</summary>
    public ImmutableArray<EventRegistration> EventRegistrations { get; }

    /// <summary>Gets the immutable client filter sequence.</summary>
    public ImmutableArray<IMessageFilter> RegisteredClientFilters { get; }

    /// <summary>Gets the immutable server filter sequence.</summary>
    public ImmutableArray<IMessageFilter> RegisteredServerFilters { get; }

    /// <summary>Gets client plans indexed by their contract type.</summary>
    public FrozenDictionary<Type, ClientPlan> ClientsByContract { get; }

    /// <summary>Gets all compiled server method routes.</summary>
    public ImmutableArray<ServerRoutePlan> ServerRoutes { get; }

    /// <summary>Gets compiled server method routes grouped by protocol.</summary>
    public FrozenDictionary<ProtocolKey, ImmutableArray<ServerRoutePlan>> ServerRoutesByProtocol { get; }

    /// <summary>Gets all compiled event routes.</summary>
    public ImmutableArray<EventPlan> EventPlans { get; }

    /// <summary>Gets compiled event routes grouped by protocol.</summary>
    public FrozenDictionary<ProtocolKey, ImmutableArray<EventPlan>> EventPlansByProtocol { get; }

    /// <summary>Gets the canonical MCP tool bindings compiled from server routes.</summary>
    public ImmutableArray<McpToolPlan> McpToolPlans { get; }

    /// <summary>Gets the optional validated large-payload policy.</summary>
    public LargePayloadOffloadPolicy? LargePayloadOffload { get; }
}

/// <summary>Describes one client contract selected for a runtime.</summary>
public sealed record ClientPlan(ClientRegistration Registration, ContractDescriptor Descriptor);

/// <summary>Describes one compiled inbound server method route.</summary>
public sealed record ServerRoutePlan(
    ServerRegistration Registration,
    ContractMethodDescriptor Method,
    ProtocolKey Protocol);

/// <summary>Describes one compiled inbound event route.</summary>
public sealed record EventPlan(EventRegistration Registration);

/// <summary>Describes one canonical MCP tool binding.</summary>
public sealed record McpToolPlan(
    string Name,
    string? Description,
    bool ReadOnly,
    bool Destructive,
    bool OpenWorld,
    ServerRegistration Registration,
    ContractMethodDescriptor Method);
