using System.Runtime.ExceptionServices;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Owns one inbound run's cancellation source and transport subscription handles.
/// </summary>
internal sealed class InboundSubscriptionHost
{
    private readonly InboundRouteTable _routes;
    private readonly InboundMessageRouter _router;
    private readonly List<ITransportSubscription> _subscriptions = [];
    private CancellationTokenSource? _runCancellation;

    public InboundSubscriptionHost(
        InboundRouteTable routes,
        InboundMessageRouter router)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public bool HasActiveRun => _runCancellation is not null || _subscriptions.Count != 0;

    public async ValueTask StartAsync(CancellationToken startupCancellation)
    {
        if (HasActiveRun)
        {
            throw new InvalidOperationException("Inbound subscriptions are already active.");
        }

        var runCancellation = new CancellationTokenSource();
        var startedSubscriptions = new List<ITransportSubscription>();
        try
        {
            foreach (var subscriptionPlan in _routes.RpcSubscriptions)
            {
                var subscription = await subscriptionPlan.SubscriptionTransport.SubscribeAsync(
                        subscriptionPlan.Channel,
                        (envelope, token) => _router.RouteRpcAsync(
                            subscriptionPlan,
                            envelope,
                            token,
                            runCancellation.Token),
                        new SubscriptionOptions
                        {
                            ConsumerGroup = $"anyprotocol.rpc.{subscriptionPlan.Channel}"
                        },
                        startupCancellation)
                    .ConfigureAwait(false);
                startedSubscriptions.Add(subscription);
            }

            foreach (var subscriptionPlan in _routes.EventSubscriptions)
            {
                var registration = subscriptionPlan.Plan.Registration;
                var subscription = await subscriptionPlan.SubscriptionTransport.SubscribeAsync(
                        registration.Channel,
                        (envelope, token) => _router.RouteEventAsync(
                            subscriptionPlan,
                            envelope,
                            token,
                            runCancellation.Token),
                        new SubscriptionOptions
                        {
                            ConsumerGroup = registration.ConsumerGroup is null
                                ? null
                                : $"{registration.ConsumerGroup}:{registration.EventType.FullName}"
                        },
                        startupCancellation)
                    .ConfigureAwait(false);
                startedSubscriptions.Add(subscription);
            }

            _subscriptions.AddRange(startedSubscriptions);
            _runCancellation = runCancellation;
        }
        catch (Exception startupException)
        {
            runCancellation.Cancel();
            var rollbackException = await DisposeSubscriptionsAsync(startedSubscriptions)
                .ConfigureAwait(false);
            if (rollbackException is not null)
            {
                _subscriptions.AddRange(startedSubscriptions);
                _runCancellation = runCancellation;
                throw new AggregateException(
                    "AnyProtocol startup failed and subscription rollback also failed.",
                    startupException,
                    rollbackException);
            }

            runCancellation.Dispose();
            ExceptionDispatchInfo.Capture(startupException).Throw();
            throw;
        }
    }

    public void CancelRun() => _runCancellation?.Cancel();

    public ValueTask<Exception?> StopAcceptingAsync()
        => StopAcceptingAsync(_subscriptions);

    public ValueTask<Exception?> DisposeSubscriptionsAsync()
        => DisposeSubscriptionsAsync(_subscriptions);

    public void CompleteRun()
    {
        if (_subscriptions.Count != 0)
        {
            throw new InvalidOperationException(
                "Cannot complete an inbound run while subscriptions remain active.");
        }

        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    private static async ValueTask<Exception?> DisposeSubscriptionsAsync(
        List<ITransportSubscription> subscriptions)
    {
        List<Exception>? exceptions = null;
        for (var index = subscriptions.Count - 1; index >= 0; index--)
        {
            try
            {
                await subscriptions[index].DisposeAsync().ConfigureAwait(false);
                subscriptions.RemoveAt(index);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return Collapse(exceptions);
    }

    private static async ValueTask<Exception?> StopAcceptingAsync(
        IReadOnlyCollection<ITransportSubscription> subscriptions)
    {
        List<Exception>? exceptions = null;
        foreach (var subscription in subscriptions.Reverse().ToArray())
        {
            try
            {
                await subscription.StopAcceptingAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
        }

        return Collapse(exceptions);
    }

    private static Exception? Collapse(List<Exception>? exceptions)
        => exceptions switch
        {
            null => null,
            [var single] => single,
            _ => new AggregateException(exceptions)
        };
}
