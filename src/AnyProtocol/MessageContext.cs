using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Carries the public message metadata for one pipeline invocation.
/// </summary>
public sealed class MessageContext : IMessageContext, IRuntimeMessageContext
{
    /// <summary>
    /// Initializes a new instance of the MessageContext class.
    /// </summary>
    /// <param name="headers">The headers.</param>
    /// <param name="body">The body.</param>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="messageType">The runtime type to resolve.</param>
    /// <param name="direction">The direction.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    public MessageContext(
        IMessageHeaders headers,
        ReadOnlyMemory<byte> body,
        string channel,
        MessageType messageType,
        MessageDirection direction,
        CancellationToken cancellationToken = default)
    {
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body;
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        MessageType = messageType;
        Direction = direction;
        Invocation = new MessageInvocation(cancellationToken);
    }

    /// <summary>
    /// Gets the metadata headers associated with the message.
    /// </summary>
    /// <value>The headers.</value>
    public IMessageHeaders Headers { get; }

    /// <summary>
    /// Gets or initializes the body.
    /// </summary>
    /// <value>The body.</value>
    public ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>
    /// Gets or initializes the message.
    /// </summary>
    /// <value>The message.</value>
    public object? Message { get; set; }

    /// <summary>
    /// Gets or initializes the channel.
    /// </summary>
    /// <value>The channel.</value>
    public string Channel { get; set; }

    /// <summary>
    /// Gets or initializes the message type.
    /// </summary>
    /// <value>The message type.</value>
    public MessageType MessageType { get; set; }

    /// <summary>
    /// Gets or initializes the direction.
    /// </summary>
    /// <value>The direction.</value>
    public MessageDirection Direction { get; set; }

    /// <summary>
    /// Gets or initializes the method.
    /// </summary>
    /// <value>The method.</value>
    public ContractMethodDescriptor? Method { get; set; }

    /// <summary>
    /// Gets the items.
    /// </summary>
    /// <value>The items.</value>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>Gets the effective cancellation token for the current pipeline stage.</summary>
    public CancellationToken CancellationToken => Invocation.CancellationToken;

    internal MessageInvocation Invocation { get; set; }

    MessageInvocation IRuntimeMessageContext.Invocation => Invocation;
}
