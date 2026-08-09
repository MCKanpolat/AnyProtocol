using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines the capability to perform native request/reply operations.
/// </summary>
public interface IRequestReplyTransport : IMessagingProtocol
{
    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the response envelope.</returns>
    ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        CancellationToken cancellationToken = default);
}
