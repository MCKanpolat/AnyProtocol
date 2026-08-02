using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Carries the message, headers, services, and response state for one pipeline invocation.
/// </summary>
public sealed class MessageContext : IMessageContext
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
        CancellationToken = cancellationToken;
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
    /// Gets or initializes the services.
    /// </summary>
    /// <value>The services.</value>
    public IDependencyResolver? Services { get; set; }

    /// <summary>
    /// Gets the items.
    /// </summary>
    /// <value>The items.</value>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or initializes the cancellation token.
    /// </summary>
    /// <value>The cancellation token.</value>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Gets the response envelope produced by the operation.
    /// </summary>
    /// <value>The response.</value>
    public TransportEnvelope? Response { get; set; }

    /// <summary>
    /// Gets or initializes the exception.
    /// </summary>
    /// <value>The exception.</value>
    public Exception? Exception { get; set; }
}
