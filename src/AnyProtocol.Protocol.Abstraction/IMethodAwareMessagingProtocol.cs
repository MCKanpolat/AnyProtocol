using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines messaging operations that receive the logical contract method metadata.
/// </summary>
public interface IMethodAwareMessagingProtocol
{
    /// <summary>
    /// Sends a transport envelope using the specified contract method metadata.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="method">The contract method metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        ContractMethodDescriptor method,
        CancellationToken cancellationToken = default);
}
