using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Routes an admitted transport callback to the matching request, stream, or event dispatcher.
/// </summary>
internal sealed class InboundMessageRouter
{
    private readonly MessageDispatcher _dispatcher;
    private readonly IRequestAdmission _admission;
    private readonly ILogWriter _logger;

    public InboundMessageRouter(
        MessageDispatcher dispatcher,
        IRequestAdmission admission,
        ILogWriter logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask RouteRpcAsync(
        InboundRpcSubscriptionPlan subscription,
        TransportEnvelope envelope,
        CancellationToken callbackCancellation,
        CancellationToken runCancellation)
    {
        var admissionLease = _admission.TryEnter();
        if (admissionLease is null)
        {
            _logger.LogEvent(
                AnyProtocolLogEvents.RoutingFailed,
                LogSeverity.Warning,
                "Rejected message on '{0}' because the bus is draining.",
                null,
                subscription.Channel);
            throw new MessageAdmissionRejectedException();
        }

        using (admissionLease)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   callbackCancellation,
                   runCancellation))
        {
            var dispatchCancellation = linkedCancellation.Token;
            var contractName = envelope.Headers[HeaderNames.Contract];
            var methodName = envelope.Headers[HeaderNames.Method];
            if (!subscription.TryGetRoute(contractName, methodName, out var route))
            {
                await _dispatcher.DispatchRoutingFaultAsync(
                        envelope,
                        subscription.Transport,
                        $"No handler is registered for '{contractName}.{methodName}'.",
                        dispatchCancellation)
                    .ConfigureAwait(false);
                return;
            }

            if (route.Method.Operation == ContractOperation.Stream)
            {
                await RouteStreamAsync(
                        subscription,
                        route,
                        envelope,
                        dispatchCancellation,
                        callbackCancellation,
                        runCancellation)
                    .ConfigureAwait(false);
                return;
            }

            await _dispatcher.DispatchAsync(
                    route.Registration,
                    route.Method,
                    envelope,
                    subscription.Transport,
                    dispatchCancellation,
                    route.Protocol)
                .ConfigureAwait(false);
        }
    }

    public ValueTask RouteEventAsync(
        InboundEventSubscriptionPlan subscription,
        TransportEnvelope envelope,
        CancellationToken callbackCancellation,
        CancellationToken runCancellation)
    {
        var admissionLease = _admission.TryEnter();
        if (admissionLease is null)
        {
            _logger.LogEvent(
                AnyProtocolLogEvents.RoutingFailed,
                LogSeverity.Warning,
                "Rejected event on '{0}' because the bus is draining.",
                null,
                subscription.Plan.Registration.Channel);
            return ValueTask.FromException(new MessageAdmissionRejectedException());
        }

        return RouteAdmittedEventAsync(
            admissionLease,
            subscription,
            envelope,
            callbackCancellation,
            runCancellation);
    }

    private async ValueTask RouteStreamAsync(
        InboundRpcSubscriptionPlan subscription,
        ServerRoutePlan route,
        TransportEnvelope envelope,
        CancellationToken dispatchCancellation,
        CancellationToken callbackCancellation,
        CancellationToken runCancellation)
    {
        var replyTo = envelope.Headers[HeaderNames.ReplyTo];
        if (string.IsNullOrWhiteSpace(replyTo))
        {
            _logger.LogEvent(
                AnyProtocolLogEvents.RoutingFailed,
                LogSeverity.Error,
                "Ignored streaming request '{0}.{1}' without a reply channel.",
                null,
                route.Method.ContractName,
                route.Method.MethodName);
            return;
        }

        try
        {
            await foreach (var response in _dispatcher.DispatchStreamAsync(
                               route.Registration,
                               route.Method,
                               envelope,
                               subscription.Transport,
                               dispatchCancellation,
                               route.Protocol)
                           .ConfigureAwait(false))
            {
                await subscription.SendTransport.SendAsync(
                        replyTo,
                        response,
                        dispatchCancellation)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (callbackCancellation.IsCancellationRequested ||
                  runCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogEvent(
                AnyProtocolLogEvents.StreamDeliveryFailed,
                LogSeverity.Error,
                "Failed to deliver stream '{0}.{1}'; exception type '{2}'.",
                exception,
                route.Method.ContractName,
                route.Method.MethodName,
                exception.GetType().FullName);
        }
    }

    private async ValueTask RouteAdmittedEventAsync(
        IAdmissionLease admissionLease,
        InboundEventSubscriptionPlan subscription,
        TransportEnvelope envelope,
        CancellationToken callbackCancellation,
        CancellationToken runCancellation)
    {
        using (admissionLease)
        using (var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                   callbackCancellation,
                   runCancellation))
        {
            var registration = subscription.Plan.Registration;
            var messageType = envelope.Headers.Get(HeaderNames.MessageType, MessageType.Event);
            var contractName = envelope.Headers[HeaderNames.Contract];
            var expectedContract = registration.EventType.FullName ?? registration.EventType.Name;
            if (messageType != MessageType.Event ||
                !string.Equals(contractName, expectedContract, StringComparison.Ordinal))
            {
                _logger.LogEvent(
                    AnyProtocolLogEvents.RoutingFailed,
                    LogSeverity.Warning,
                    "Ignored unmatched AnyProtocol event on channel '{0}'.",
                    null,
                    registration.Channel);
                return;
            }

            await _dispatcher.DispatchEventAsync(
                    registration,
                    envelope,
                    subscription.Transport,
                    linkedCancellation.Token)
                .ConfigureAwait(false);
        }
    }
}
