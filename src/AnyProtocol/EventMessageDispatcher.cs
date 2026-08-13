using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Dispatches events to their registered consumers.
/// </summary>
internal sealed class EventMessageDispatcher
{
    private readonly IDependencyResolverFactory _resolverFactory;
    private readonly IMessageSerializer _serializer;
    private readonly HandlerInvoker _handlerInvoker;
    private readonly FaultMessageSender _faultMessageSender;
    private readonly MessageFilterDelegate _pipeline;

    public EventMessageDispatcher(
        IDependencyResolverFactory resolverFactory,
        IMessageSerializer serializer,
        IEnumerable<IMessageFilter> filters,
        HandlerInvoker handlerInvoker,
        FaultMessageSender faultMessageSender)
    {
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _handlerInvoker = handlerInvoker ?? throw new ArgumentNullException(nameof(handlerInvoker));
        _faultMessageSender = faultMessageSender ??
                              throw new ArgumentNullException(nameof(faultMessageSender));
        _pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            _handlerInvoker.InvokeAsync);
    }

    public async ValueTask DispatchAsync(
        EventRegistration registration,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(transport);

        try
        {
            await using var scope = _resolverFactory.CreateAsyncScope();
            var message = await _serializer.DeserializeAsync(
                    registration.EventType,
                    envelope.Body,
                    cancellationToken)
                .ConfigureAwait(false) ??
                throw new InvalidOperationException(
                    $"Event body for '{registration.EventType.FullName}' was null.");
            var context = new MessageContext(
                envelope.Headers,
                envelope.Body,
                registration.Channel,
                MessageType.Event,
                MessageDirection.Inbound,
                cancellationToken)
            {
                Message = message,
                Invocation = new MessageInvocation(
                    cancellationToken,
                    scope.Resolver,
                    new DispatchState(null, registration, transport))
            };
            context.Items[DiagnosticContext.TransportNameKey] = registration.TransportName;
            await _pipeline(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (await _faultMessageSender.TrySendToDeadLetterAsync(
                    envelope,
                    method: null,
                    transport,
                    registration.Channel,
                    registration.EventType.FullName ?? registration.EventType.Name,
                    methodName: null,
                    registration.TransportName,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return;
            }

            await _faultMessageSender.SendAsync(envelope, null, transport, exception, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
