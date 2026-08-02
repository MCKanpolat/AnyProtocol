using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the raw bus client implementation used by AnyProtocol applications.
/// </summary>
public sealed class RawBusClient : IAsyncDisposable
{
    private readonly RequestReplyEngine _engine;

    /// <summary>
    /// Initializes a new instance of the RawBusClient class.
    /// </summary>
    /// <param name="transport">The transport.</param>
    public RawBusClient(IMessagingProtocol transport)
    {
        _engine = new RequestReplyEngine(transport);
    }

    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the request async.</returns>
    public ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => _engine.RequestAsync(channel, request, timeout, cancellationToken);

    /// <summary>
    /// Sends a transport envelope to the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask SendAsync(
        string channel,
        TransportEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(message);

        var headers = new MessageHeaders(message.Headers);
        headers[HeaderNames.Channel] = channel;
        headers[HeaderNames.MessageType] = MessageType.Event.ToString();
        headers[HeaderNames.MessageId] ??= Guid.NewGuid().ToString("N");

        return _engine.PublishAsync(
            channel,
            new TransportEnvelope(headers, message.Body),
            cancellationToken);
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
