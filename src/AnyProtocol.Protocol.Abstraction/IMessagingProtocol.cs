using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines operations for messaging.
/// </summary>
public interface IMessagingProtocol : IAsyncDisposable
{
    /// <summary>
    /// Gets the capabilities.
    /// </summary>
    /// <value>The capabilities.</value>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the transport's delivery, ordering, durability, and flow-control semantics.
    /// Existing transports receive conservative defaults derived from
    /// <see cref="Capabilities"/>.
    /// </summary>
    TransportSemantics Semantics => TransportSemantics.FromCapabilities(Capabilities);

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

    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default);
}