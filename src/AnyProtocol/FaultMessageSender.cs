using AnyProtocol.Abstraction;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Creates, reports, and delivers fault envelopes for the dispatcher.
/// </summary>
internal sealed class FaultMessageSender
{
    private readonly IMessageSerializer _serializer;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly IReadOnlyList<IErrorHandler> _errorHandlers;
    private readonly ILogWriter _logger;

    public FaultMessageSender(
        IMessageSerializer serializer,
        IMessageEnvelopeFactory envelopeFactory,
        IEnumerable<IErrorHandler> errorHandlers,
        ILogWriterFactory logWriterFactory)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _errorHandlers = (errorHandlers ?? []).ToArray();
        _logger = (logWriterFactory ?? throw new ArgumentNullException(nameof(logWriterFactory)))
            .CreateLogWriter(nameof(FaultMessageSender));
    }

    public async ValueTask SendAsync(
        TransportEnvelope request,
        ContractMethodDescriptor? method,
        IMessagingProtocol transport,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(exception);

        var replyTo = request.Headers[HeaderNames.ReplyTo];
        await NotifyErrorHandlersAsync(request, method, exception, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(replyTo))
        {
            if (_errorHandlers.Count == 0)
            {
                _logger.LogEvent(
                    AnyProtocolLogEvents.UnhandledMessageError,
                    LogSeverity.Error,
                    "Unhandled AnyProtocol event error for '{0}.{1}'; exception type '{2}'.",
                    exception,
                    method?.ContractName ?? request.Headers[HeaderNames.Contract] ?? "unknown",
                    method?.MethodName ?? request.Headers[HeaderNames.Method] ?? "unknown",
                    exception.GetType().FullName);
            }

            return;
        }

        var fault = CreateFault(exception);
        var body = await _serializer.SerializeAsync(fault, cancellationToken).ConfigureAwait(false);
        try
        {
            var sendTransport = transport as ISendTransport ??
                throw new InvalidOperationException(
                    "Sending a fault requires an ISendTransport implementation.");
            await sendTransport.SendAsync(
                    replyTo,
                    new TransportEnvelope(
                        _envelopeFactory.CreateResponseHeaders(request.Headers, MessageType.Fault),
                        body),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception deliveryException) when (deliveryException is not OperationCanceledException)
        {
            _logger.LogEvent(
                AnyProtocolLogEvents.FaultDeliveryFailed,
                LogSeverity.Error,
                "Failed to deliver an AnyProtocol fault response; exception type '{0}'.",
                deliveryException,
                deliveryException.GetType().FullName);
        }
    }

    public async ValueTask<TransportEnvelope> CreateStreamFaultAsync(
        TransportEnvelope request,
        ContractMethodDescriptor method,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await NotifyErrorHandlersAsync(request, method, exception, cancellationToken)
            .ConfigureAwait(false);
        var fault = CreateFault(exception);
        var body = await _serializer.SerializeAsync(fault, cancellationToken).ConfigureAwait(false);
        return new TransportEnvelope(
            _envelopeFactory.CreateResponseHeaders(request.Headers, MessageType.Fault),
            body);
    }

    public async ValueTask<bool> TrySendToDeadLetterAsync(
        TransportEnvelope request,
        ContractMethodDescriptor? method,
        IMessagingProtocol transport,
        string channel,
        string contractName,
        string? methodName,
        string protocolName,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (transport is not IDeadLetterTransport deadLetterTransport)
        {
            return false;
        }

        await NotifyErrorHandlersAsync(request, method, exception, cancellationToken)
            .ConfigureAwait(false);
        await deadLetterTransport.SendToDeadLetterAsync(
                channel,
                request,
                exception,
                cancellationToken)
            .ConfigureAwait(false);
        AnyProtocolDiagnostics.RecordDeadLetter(
            request.Headers,
            contractName,
            methodName,
            protocolName);
        return true;
    }

    public async ValueTask NotifyErrorHandlersAsync(
        TransportEnvelope request,
        ContractMethodDescriptor? method,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (_errorHandlers.Count == 0)
        {
            return;
        }

        var context = new MessageContext(
            request.Headers,
            request.Body,
            method?.Channel ?? request.Headers[HeaderNames.Channel] ?? string.Empty,
            request.Headers.Get(HeaderNames.MessageType, MessageType.Event),
            MessageDirection.Inbound,
            cancellationToken)
        {
            Method = method
        };
        var errorContext = new ErrorContext(context, exception);
        foreach (var errorHandler in _errorHandlers)
        {
            try
            {
                await errorHandler.HandleAsync(errorContext).ConfigureAwait(false);
            }
            catch (Exception handlerException)
            {
                _logger.LogEvent(
                    AnyProtocolLogEvents.ErrorHandlerFailed,
                    LogSeverity.Error,
                    "AnyProtocol error handler '{0}' failed while reporting '{1}'; " +
                    "exception type '{2}'.",
                    handlerException,
                    errorHandler.GetType().FullName,
                    exception.GetType().FullName,
                    handlerException.GetType().FullName);
            }
        }
    }

    internal static FaultMessage CreateFault(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            AnyProtocolFaultException knownFault => knownFault.Fault,
            LargePayloadException payload => new FaultMessage(
                payload.Code,
                payload.Message,
                payload.GetType().FullName,
                payload.Retryable),
            _ => new FaultMessage(
                "handler_failed",
                exception.Message,
                exception.GetType().FullName,
                Retryable: false)
        };
    }
}
