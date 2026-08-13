namespace AnyProtocol.Abstraction;

/// <summary>
/// Creates message metadata without owning payload serialization or transport identity.
/// </summary>
public interface IMessageEnvelopeFactory
{
    /// <summary>
    /// Creates a message identifier for an outbound envelope.
    /// </summary>
    /// <returns>A non-empty message identifier.</returns>
    string CreateMessageId();

    /// <summary>
    /// Gets the current UTC time used for protocol metadata and deadlines.
    /// </summary>
    /// <returns>The current UTC time.</returns>
    DateTimeOffset GetUtcNow();

    /// <summary>
    /// Creates outbound headers with the standard AnyProtocol metadata.
    /// </summary>
    /// <param name="messageType">The message type.</param>
    /// <param name="channel">The logical channel.</param>
    /// <param name="contractName">The optional contract name.</param>
    /// <param name="methodName">The optional method name.</param>
    /// <param name="contentType">The optional content type.</param>
    /// <param name="source">Optional headers to copy before applying protocol metadata.</param>
    /// <param name="correlationId">The optional correlation identifier.</param>
    /// <param name="replyTo">The optional reply channel.</param>
    /// <returns>The created headers.</returns>
    IMessageHeaders CreateOutboundHeaders(
        MessageType messageType,
        string channel,
        string? contractName = null,
        string? methodName = null,
        string? contentType = null,
        IMessageHeaders? source = null,
        string? correlationId = null,
        string? replyTo = null);

    /// <summary>
    /// Creates response, stream, or fault headers for an existing request.
    /// </summary>
    /// <param name="requestHeaders">The request headers.</param>
    /// <param name="messageType">The response message type.</param>
    /// <returns>The created response headers.</returns>
    IMessageHeaders CreateResponseHeaders(
        IMessageHeaders requestHeaders,
        MessageType messageType);
}
