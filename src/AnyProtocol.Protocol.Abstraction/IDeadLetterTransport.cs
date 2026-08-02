using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines operations for dead letter transport.
/// </summary>
public interface IDeadLetterTransport
{
    /// <summary>
    /// Sends a failed message to the channel configured for dead-letter delivery.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask SendToDeadLetterAsync(
        string channel,
        TransportEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken = default);
}
