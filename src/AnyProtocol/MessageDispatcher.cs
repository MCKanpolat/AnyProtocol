using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Services;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Composes the request, stream, event, and fault dispatch paths.
/// </summary>
public sealed class MessageDispatcher
{
    private readonly RequestMessageDispatcher _requestDispatcher;
    private readonly StreamMessageDispatcher _streamDispatcher;
    private readonly EventMessageDispatcher _eventDispatcher;
    private readonly FaultMessageSender _faultMessageSender;
    private readonly LargePayloadMaterializer _payloadMaterializer;
    private readonly LargePayloadOffloader _payloadOffloader;

    /// <summary>
    /// Initializes a new instance of the MessageDispatcher class.
    /// </summary>
    /// <param name="resolverFactory">The resolver factory.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="filters">The filters.</param>
    /// <param name="errorHandlers">The error handlers.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="logWriterFactory">The logging writer factory.</param>
    /// <param name="payloadMaterializer">The optional inbound payload processor.</param>
    /// <param name="payloadOffloader">The optional outbound payload processor.</param>
    public MessageDispatcher(
        IDependencyResolverFactory resolverFactory,
        IMessageSerializer serializer,
        IEnumerable<IMessageFilter>? filters = null,
        IEnumerable<IErrorHandler>? errorHandlers = null,
        IMessageEnvelopeFactory? envelopeFactory = null,
        ILogWriterFactory? logWriterFactory = null,
        LargePayloadMaterializer? payloadMaterializer = null,
        LargePayloadOffloader? payloadOffloader = null)
    {
        ArgumentNullException.ThrowIfNull(resolverFactory);
        ArgumentNullException.ThrowIfNull(serializer);

        var configuredFilters = (filters ?? []).ToArray();
        var messageEnvelopeFactory = envelopeFactory ??
                                     DefaultMessageEnvelopeFactory.CreateDefault();
        var stores = new LargePayloadStoreRegistry([]);
        var offloader = payloadOffloader ?? new LargePayloadOffloader(null, stores);
        var replyMessageSender = new ReplyMessageSender(serializer, messageEnvelopeFactory, offloader);
        var handlerInvoker = new HandlerInvoker();
        _faultMessageSender = new FaultMessageSender(
            serializer,
            messageEnvelopeFactory,
            errorHandlers ?? [],
            logWriterFactory ?? NullLogWriterFactory.Instance);
        _requestDispatcher = new RequestMessageDispatcher(
            resolverFactory,
            serializer,
            configuredFilters,
            handlerInvoker,
            replyMessageSender,
            _faultMessageSender);
        _streamDispatcher = new StreamMessageDispatcher(
            resolverFactory,
            serializer,
            messageEnvelopeFactory,
            configuredFilters,
            new DeadlineCancellationFactory(messageEnvelopeFactory),
            handlerInvoker,
            _faultMessageSender);
        _eventDispatcher = new EventMessageDispatcher(
            resolverFactory,
            serializer,
            configuredFilters,
            handlerInvoker,
            _faultMessageSender);
        _payloadMaterializer = payloadMaterializer ?? new LargePayloadMaterializer(
            new LargePayloadStoreRegistry([]),
            new DefaultDateTimeProvider());
        _payloadOffloader = offloader;
    }

    /// <summary>
    /// Performs the dispatch async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="method">The method.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DispatchAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken = default,
        ProtocolKey? protocol = null)
    {
        TransportEnvelope materialized;
        try
        {
            materialized = await _payloadMaterializer.MaterializeAsync(envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LargePayloadException exception)
        {
            await _faultMessageSender.SendAsync(
                    envelope,
                    method,
                    transport,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        await _requestDispatcher.DispatchAsync(
            registration,
            method,
            materialized,
            transport,
            cancellationToken,
            protocol).ConfigureAwait(false);
    }

    internal async ValueTask<DispatchInvocationResult> InvokeAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default,
        ProtocolKey? protocol = null)
    {
        TransportEnvelope materialized;
        try
        {
            materialized = await _payloadMaterializer.MaterializeAsync(envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LargePayloadException exception)
        {
            return new DispatchInvocationResult(null, FaultMessageSender.CreateFault(exception));
        }
        return await _requestDispatcher.InvokeAsync(
            registration,
            method,
            materialized,
            cancellationToken,
            protocol).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs the dispatch stream async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="method">The method.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    public async IAsyncEnumerable<TransportEnvelope> DispatchStreamAsync(
        ServerRegistration registration,
        ContractMethodDescriptor method,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default,
        ProtocolKey? protocol = null)
    {
        TransportEnvelope? materialized = null;
        TransportEnvelope? materializationFault = null;
        try
        {
            materialized = await _payloadMaterializer.MaterializeAsync(envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LargePayloadException exception)
        {
            materializationFault = await _faultMessageSender.CreateStreamFaultAsync(
                    envelope,
                    method,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (materializationFault is not null)
        {
            yield return materializationFault;
            yield break;
        }

        long totalBytes = 0;
        await foreach (var item in _streamDispatcher.DispatchAsync(
            registration,
            method,
            materialized!,
            transport,
            cancellationToken,
            protocol).ConfigureAwait(false))
        {
            totalBytes += item.Body.Length;
            if (_payloadOffloader.Policy is { } policy &&
                totalBytes > policy.MaxStoredPayloadBytes)
            {
                throw new LargePayloadException(
                    LargePayloadFailureCodes.TooLarge,
                    $"The stream exceeded its configured total serialized size of " +
                    $"{policy.MaxStoredPayloadBytes} bytes.");
            }

            yield return await _payloadOffloader.OffloadAsync(item, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Performs the dispatch routing fault async operation.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DispatchRoutingFaultAsync(
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        string message,
        CancellationToken cancellationToken = default)
        => _faultMessageSender.SendAsync(
            envelope,
            method: null,
            transport,
            new AnyProtocolFaultException(new FaultMessage("route_not_found", message)),
            cancellationToken);

    /// <summary>
    /// Performs the dispatch event async operation.
    /// </summary>
    /// <param name="registration">The registration.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="transport">The transport.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DispatchEventAsync(
        EventRegistration registration,
        TransportEnvelope envelope,
        IMessagingProtocol transport,
        CancellationToken cancellationToken = default)
    {
        TransportEnvelope materialized;
        try
        {
            materialized = await _payloadMaterializer.MaterializeAsync(envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LargePayloadException exception)
        {
            if (exception.Retryable)
            {
                throw;
            }

            var deadLettered = await _faultMessageSender.TrySendToDeadLetterAsync(
                    envelope,
                    method: null,
                    transport,
                    registration.Channel,
                    registration.EventType.FullName ?? registration.EventType.Name,
                    methodName: null,
                    registration.TransportName,
                    exception,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!deadLettered)
            {
                await _faultMessageSender.SendAsync(
                        envelope,
                        method: null,
                        transport,
                        exception,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            return;
        }
        await _eventDispatcher.DispatchAsync(
            registration,
            materialized,
            transport,
            cancellationToken).ConfigureAwait(false);
    }

}
