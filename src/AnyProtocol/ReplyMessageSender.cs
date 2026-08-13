using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Serializes and sends successful replies for the dispatcher.
/// </summary>
internal sealed class ReplyMessageSender
{
    private readonly IMessageSerializer _serializer;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly LargePayloadOffloader _payloadOffloader;

    public ReplyMessageSender(
        IMessageSerializer serializer,
        IMessageEnvelopeFactory envelopeFactory,
        LargePayloadOffloader payloadOffloader)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _envelopeFactory = envelopeFactory ?? throw new ArgumentNullException(nameof(envelopeFactory));
        _payloadOffloader = payloadOffloader ?? throw new ArgumentNullException(nameof(payloadOffloader));
    }

    public async ValueTask SendAsync(
        TransportEnvelope request,
        ContractMethodDescriptor method,
        IMessagingProtocol transport,
        object? response,
        CancellationToken cancellationToken)
    {
        var replyTo = request.Headers[HeaderNames.ReplyTo] ??
                      throw new InvalidOperationException(
                          $"Request '{method.ContractName}.{method.MethodName}' has no reply channel.");
        var headers = _envelopeFactory.CreateResponseHeaders(request.Headers, MessageType.Response);
        var body = response is null
            ? await _serializer.SerializeAsync<object?>(null, cancellationToken).ConfigureAwait(false)
            : await _serializer.SerializeAsync(response.GetType(), response, cancellationToken)
                .ConfigureAwait(false);
        var sendTransport = transport as ISendTransport ??
            throw new InvalidOperationException(
                "Sending a response requires an ISendTransport implementation.");
        var envelope = await _payloadOffloader.OffloadAsync(
                new TransportEnvelope(headers, body),
                cancellationToken)
            .ConfigureAwait(false);
        await sendTransport.SendAsync(
                replyTo,
                envelope,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
