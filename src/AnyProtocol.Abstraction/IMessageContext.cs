using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for message context.
/// </summary>
public interface IMessageContext
{
    /// <summary>
    /// Gets the headers.
    /// </summary>
    /// <value>The headers.</value>
    IMessageHeaders Headers { get; }

    /// <summary>
    /// Gets the body.
    /// </summary>
    /// <value>The body.</value>
    ReadOnlyMemory<byte> Body { get; set; }

    /// <summary>
    /// Gets the message.
    /// </summary>
    /// <value>The message.</value>
    object? Message { get; set; }

    /// <summary>
    /// Gets the channel.
    /// </summary>
    /// <value>The channel.</value>
    string Channel { get; set; }

    /// <summary>
    /// Gets the message type.
    /// </summary>
    /// <value>The message type.</value>
    MessageType MessageType { get; set; }

    /// <summary>
    /// Gets the direction.
    /// </summary>
    /// <value>The direction.</value>
    MessageDirection Direction { get; set; }

    /// <summary>
    /// Gets the method.
    /// </summary>
    /// <value>The method.</value>
    ContractMethodDescriptor? Method { get; set; }

    /// <summary>
    /// Gets the services.
    /// </summary>
    /// <value>The services.</value>
    IDependencyResolver? Services { get; set; }

    /// <summary>
    /// Gets the items.
    /// </summary>
    /// <value>The items.</value>
    IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets the cancellation token.
    /// </summary>
    /// <value>The cancellation token.</value>
    CancellationToken CancellationToken { get; set; }

    /// <summary>
    /// Gets the response.
    /// </summary>
    /// <value>The response.</value>
    TransportEnvelope? Response { get; set; }

    /// <summary>
    /// Gets the exception.
    /// </summary>
    /// <value>The exception.</value>
    Exception? Exception { get; set; }
}