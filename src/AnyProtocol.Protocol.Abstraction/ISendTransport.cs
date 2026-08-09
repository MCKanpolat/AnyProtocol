using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines the capability to send envelopes to logical channels.
/// </summary>
public interface ISendTransport : IMessagingProtocol
{
    /// <summary>
    /// Sends a transport envelope to the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default);
}
