using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines operations for native request reply transport.
/// </summary>
public interface INativeRequestReplyTransport
{
    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        CancellationToken cancellationToken = default);
}
