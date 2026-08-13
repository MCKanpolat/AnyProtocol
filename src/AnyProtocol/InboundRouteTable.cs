using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the immutable subscription and route lookup used by the inbound transport host.
/// </summary>
internal sealed class InboundRouteTable
{
    public InboundRouteTable(RuntimePlan runtimePlan, TransportRegistry transports)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        ArgumentNullException.ThrowIfNull(transports);

        RpcSubscriptions = runtimePlan.ServerRoutes
            .Where(static route => route.Protocol != ProtocolKey.Mcp)
            .Select(route => (Route: route, Transport: transports.GetRequired(route.Protocol)))
            .Where(static binding => binding.Transport is not INativeServerTransport)
            .GroupBy(static binding => (binding.Route.Protocol, binding.Route.Method.Channel))
            .Select(static group => CreateRpcSubscription(group.Key, group))
            .ToImmutableArray();

        EventSubscriptions = runtimePlan.EventPlans
            .Select(plan => (
                Plan: plan,
                Transport: transports.GetRequired(plan.Registration.Protocol)))
            .Where(static binding => binding.Transport is not INativeServerTransport)
            .Select(static binding => CreateEventSubscription(binding.Plan, binding.Transport))
            .ToImmutableArray();
    }

    public ImmutableArray<InboundRpcSubscriptionPlan> RpcSubscriptions { get; }

    public ImmutableArray<InboundEventSubscriptionPlan> EventSubscriptions { get; }

    private static InboundRpcSubscriptionPlan CreateRpcSubscription(
        (ProtocolKey Protocol, string Channel) key,
        IEnumerable<(ServerRoutePlan Route, IMessagingProtocol Transport)> bindings)
    {
        var entries = bindings.ToArray();
        var transport = entries[0].Transport;
        var subscriptionTransport = transport as ISubscriptionTransport ??
                                    throw new InvalidOperationException(
                                        $"Transport '{key.Protocol}' does not implement " +
                                        $"{nameof(ISubscriptionTransport)}.");
        var sendTransport = transport as ISendTransport ??
                            throw new InvalidOperationException(
                                $"Transport '{key.Protocol}' does not implement " +
                                $"{nameof(ISendTransport)}.");
        var routes = entries
            .Select(static entry => entry.Route)
            .ToFrozenDictionary(
                static route => new InboundRouteKey(
                    route.Method.ContractName,
                    route.Method.MethodName));
        return new InboundRpcSubscriptionPlan(
            key.Protocol,
            key.Channel,
            transport,
            subscriptionTransport,
            sendTransport,
            routes);
    }

    private static InboundEventSubscriptionPlan CreateEventSubscription(
        EventPlan plan,
        IMessagingProtocol transport)
    {
        var subscriptionTransport = transport as ISubscriptionTransport ??
                                    throw new InvalidOperationException(
                                        $"Transport '{plan.Registration.Protocol}' does not implement " +
                                        $"{nameof(ISubscriptionTransport)}.");
        return new InboundEventSubscriptionPlan(plan, transport, subscriptionTransport);
    }
}

internal sealed class InboundRpcSubscriptionPlan(
    ProtocolKey protocol,
    string channel,
    IMessagingProtocol transport,
    ISubscriptionTransport subscriptionTransport,
    ISendTransport sendTransport,
    FrozenDictionary<InboundRouteKey, ServerRoutePlan> routes)
{
    public ProtocolKey Protocol { get; } = protocol;

    public string Channel { get; } = channel;

    public IMessagingProtocol Transport { get; } = transport;

    public ISubscriptionTransport SubscriptionTransport { get; } = subscriptionTransport;

    public ISendTransport SendTransport { get; } = sendTransport;

    public bool TryGetRoute(
        string? contractName,
        string? methodName,
        [NotNullWhen(true)]
        out ServerRoutePlan? route)
    {
        if (contractName is null || methodName is null)
        {
            route = null;
            return false;
        }

        return routes.TryGetValue(new InboundRouteKey(contractName, methodName), out route);
    }
}

internal sealed record InboundEventSubscriptionPlan(
    EventPlan Plan,
    IMessagingProtocol Transport,
    ISubscriptionTransport SubscriptionTransport);

internal readonly record struct InboundRouteKey(string ContractName, string MethodName);
