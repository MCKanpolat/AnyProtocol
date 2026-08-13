using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines the capability to subscribe to envelopes from logical channels.
/// </summary>
public interface ISubscriptionTransport : IMessagingProtocol
{
    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the subscription.</returns>
    /// <remarks>
    /// Durable transports should requeue or leave uncommitted a delivery when the handler throws
    /// <see cref="MessageAdmissionRejectedException"/>.
    /// </remarks>
    ValueTask<ITransportSubscription> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default);
}
