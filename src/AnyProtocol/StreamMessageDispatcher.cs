using System.Globalization;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Dispatches streaming operations and emits their wire envelopes.
/// </summary>
internal sealed class StreamMessageDispatcher
{
    private readonly IDependencyResolverFactory _resolverFactory;
    private readonly IMessageSerializer _serializer;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly DeadlineCancellationFactory _deadlineCancellationFactory;
    private readonly HandlerInvoker _handlerInvoker;
    private readonly FaultMessageSender _faultMessageSender;
    private readonly MessageFilterDelegate _pipeline;

    public StreamMessageDispatcher(
        IDependencyResolverFactory resolverFactory,
        IMessageSerializer serializer,
        IMessageEnvelopeFactory envelopeFactory,
        IEnumerable<IMessageFilter> filters,
        DeadlineCancellationFactory deadlineCancellationFactory,
        HandlerInvoker handlerInvoker,
        FaultMessageSender faultMessageSender)
    {
        _resolverFactory = resolverFactory ?? throw new ArgumentNullException(nameof(resolverFactory));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _deadlineCancellationFactory = deadlineCancellationFactory ??
                                       throw new ArgumentNullException(nameof(deadlineCancellationFactory));
        _handlerInvoker = handlerInvoker ?? throw new ArgumentNullException(nameof(handlerInvoker));
        _faultMessageSender = faultMessageSender ??
                              throw new ArgumentNullException(nameof(faultMessageSender));
        _pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(filters),
            _handlerInvoker.InvokeAsync);
    }

    public async IAsyncEnumerable<TransportEnvelope> DispatchAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ProtocolKey? protocol)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(transport);
        if (method.Operation != ContractOperation.Stream)
        {
            throw new ArgumentException(
                $"Method '{method.ContractName}.{method.MethodName}' is not a streaming operation.",
                nameof(method));
        }

        var protocolName = protocol?.Value ?? registration.TransportName;
        using var deadlineSource = _deadlineCancellationFactory.Create(envelope, cancellationToken);
        var dispatchCancellationToken = deadlineSource?.Token ?? cancellationToken;
        await using var scope = _resolverFactory.CreateAsyncScope();
        object request = method.RequestType == typeof(EmptyRequest)
            ? EmptyRequest.Instance
            : await _serializer.DeserializeAsync(
                    method.RequestType,
                    envelope.Body,
                    dispatchCancellationToken)
                  .ConfigureAwait(false) ??
              throw new InvalidOperationException(
                  $"Request body for '{method.ContractName}.{method.MethodName}' was null.");
        var context = new MessageContext(
            envelope.Headers,
            envelope.Body,
            method.Channel,
            MessageType.Request,
            MessageDirection.Inbound,
            dispatchCancellationToken)
        {
            Message = request,
            Method = method,
            Invocation = new MessageInvocation(
                dispatchCancellationToken,
                scope.Resolver,
                new DispatchState(registration, null, transport))
        };
        context.Items[DiagnosticContext.TransportNameKey] = protocolName;

        var measurement = AnyProtocolDiagnostics.StartOperation(context, protocolName, "stream");
        using var activity = TracingFilter.StartActivity(context);
        var outcome = "cancelled";
        try
        {
            Exception? startError = null;
            try
            {
                await _pipeline(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dispatchCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                startError = exception;
            }

            if (startError is not null)
            {
                outcome = AnyProtocolDiagnostics.GetOutcome(startError, dispatchCancellationToken);
                TracingFilter.SetError(activity, startError, dispatchCancellationToken);
                yield return await _faultMessageSender.CreateStreamFaultAsync(
                        envelope,
                        method,
                        startError,
                        dispatchCancellationToken)
                    .ConfigureAwait(false);
                yield break;
            }

            var stream = MessageContextRuntime.Get(context).Result.StreamResult;
            if (stream is null)
            {
                var missingStream = new InvalidOperationException(
                    $"Streaming handler '{method.ContractName}.{method.MethodName}' returned no stream.");
                outcome = "fault";
                TracingFilter.SetError(activity, missingStream, dispatchCancellationToken);
                yield return await _faultMessageSender.CreateStreamFaultAsync(
                        envelope,
                        method,
                        missingStream,
                        dispatchCancellationToken)
                    .ConfigureAwait(false);
                yield break;
            }

            await using var enumerator = stream.GetAsyncEnumerator(dispatchCancellationToken);
            long sequence = 0;
            while (true)
            {
                bool hasNext;
                object? item = null;
                Exception? iterationError = null;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasNext)
                    {
                        item = enumerator.Current;
                    }
                }
                catch (OperationCanceledException) when (dispatchCancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    hasNext = false;
                    iterationError = exception;
                }

                if (iterationError is not null)
                {
                    outcome = AnyProtocolDiagnostics.GetOutcome(
                        iterationError,
                        dispatchCancellationToken);
                    TracingFilter.SetError(activity, iterationError, dispatchCancellationToken);
                    yield return await _faultMessageSender.CreateStreamFaultAsync(
                            envelope,
                            method,
                            iterationError,
                            dispatchCancellationToken)
                        .ConfigureAwait(false);
                    yield break;
                }

                if (!hasNext)
                {
                    break;
                }

                ReadOnlyMemory<byte> body = default;
                Exception? serializationError = null;
                try
                {
                    body = item is null
                        ? await _serializer.SerializeAsync<object?>(
                                null,
                                dispatchCancellationToken)
                            .ConfigureAwait(false)
                        : await _serializer.SerializeAsync(
                                item.GetType(),
                                item,
                                dispatchCancellationToken)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    serializationError = exception;
                }

                if (serializationError is not null)
                {
                    outcome = "fault";
                    TracingFilter.SetError(activity, serializationError, dispatchCancellationToken);
                    yield return await _faultMessageSender.CreateStreamFaultAsync(
                            envelope,
                            method,
                            serializationError,
                            dispatchCancellationToken)
                        .ConfigureAwait(false);
                    yield break;
                }

                var headers = _envelopeFactory.CreateResponseHeaders(
                    envelope.Headers,
                    MessageType.StreamItem);
                headers[HeaderNames.StreamSequence] = sequence.ToString(CultureInfo.InvariantCulture);
                yield return new TransportEnvelope(headers, body);
                sequence++;
            }

            outcome = "success";
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
            yield return new TransportEnvelope(
                _envelopeFactory.CreateResponseHeaders(
                    envelope.Headers,
                    MessageType.StreamComplete),
                ReadOnlyMemory<byte>.Empty);
        }
        finally
        {
            measurement.Complete(outcome);
        }
    }
}
