using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines the capability to perform native streaming operations.
/// </summary>
public interface IStreamingTransport : IMessagingProtocol
{
    /// <summary>
    /// Sends a request and asynchronously yields the streamed response items.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of response envelopes.</returns>
    IAsyncEnumerable<TransportEnvelope> StreamAsync(
        string channel,
        TransportEnvelope request,
        CancellationToken cancellationToken = default);
}
