using System.Reflection;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class InboundArchitectureTests
{
    [Fact]
    public void Route_table_groups_methods_and_indexes_exact_contract_routes()
    {
        var transport = new SubscriptionTransport();
        var plan = new LinkBuilder()
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("inbound", transport)
            .AddServer<IInboundRouteContract, InboundRouteService>(
                options => options.UseTransport("inbound"))
            .AddEventHandler<InboundEvent, InboundEventHandler>(
                options => options
                    .UseTransport("inbound")
                    .UseChannel("inbound.events"))
            .Build();

        var routes = new InboundRouteTable(
            plan,
            new TransportRegistry(plan.RegisteredTransports));

        var rpc = Assert.Single(routes.RpcSubscriptions);
        Assert.Equal("inbound.shared", rpc.Channel);
        Assert.True(rpc.TryGetRoute(
            typeof(IInboundRouteContract).FullName,
            nameof(IInboundRouteContract.FirstAsync),
            out var first));
        Assert.Equal(ContractOperation.Request, first.Method.Operation);
        Assert.True(rpc.TryGetRoute(
            typeof(IInboundRouteContract).FullName,
            nameof(IInboundRouteContract.SecondAsync),
            out _));
        Assert.False(rpc.TryGetRoute(
            typeof(IInboundRouteContract).FullName,
            "MissingAsync",
            out _));

        var eventSubscription = Assert.Single(routes.EventSubscriptions);
        Assert.Equal("inbound.events", eventSubscription.Plan.Registration.Channel);
        Assert.Same(transport, eventSubscription.Transport);
    }

    [Fact]
    public void Bus_delegates_inbound_routes_and_subscription_ownership()
    {
        var fieldTypes = typeof(AnyProtocolBus)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static field => field.FieldType)
            .ToArray();

        Assert.Contains(typeof(InboundSubscriptionHost), fieldTypes);
        Assert.DoesNotContain(typeof(RuntimePlan), fieldTypes);
        Assert.DoesNotContain(typeof(MessageDispatcher), fieldTypes);
        Assert.DoesNotContain(typeof(CancellationTokenSource), fieldTypes);
        Assert.DoesNotContain(
            fieldTypes,
            static type => type.IsGenericType &&
                           type.GetGenericTypeDefinition() == typeof(List<>) &&
                           type.GenericTypeArguments[0] == typeof(ITransportSubscription));
    }

    public sealed record InboundRequest(string Value);

    public sealed record InboundResponse(string Value);

    public sealed record InboundEvent(string Value);

    public interface IInboundRouteContract
    {
        [Channel("inbound.shared")]
        ValueTask<InboundResponse> FirstAsync(InboundRequest request);

        [Channel("inbound.shared")]
        ValueTask<InboundResponse> SecondAsync(InboundRequest request);
    }

    public sealed class InboundRouteService : IInboundRouteContract
    {
        public ValueTask<InboundResponse> FirstAsync(InboundRequest request)
            => ValueTask.FromResult(new InboundResponse(request.Value));

        public ValueTask<InboundResponse> SecondAsync(InboundRequest request)
            => ValueTask.FromResult(new InboundResponse(request.Value));
    }

    public sealed class InboundEventHandler : IEventConsumer<InboundEvent>
    {
        public ValueTask ConsumeAsync(InboundEvent @event) => ValueTask.CompletedTask;
    }

    private sealed class SubscriptionTransport : ISendTransport, ISubscriptionTransport
    {
        public TransportCapabilities Capabilities => TransportCapabilities.CompetingConsumers;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<ITransportSubscription> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
