using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines native request-reply operations that receive contract method metadata.
/// </summary>
public interface IMethodAwareRequestReplyTransport
{
    /// <summary>
    /// Sends a request using the specified contract method metadata and waits for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request envelope.</param>
    /// <param name="method">The contract method metadata.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task whose result contains the response envelope.</returns>
    ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        ContractMethodDescriptor method,
        CancellationToken cancellationToken = default);
}
