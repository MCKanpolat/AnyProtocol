namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for event publisher.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventPublisher<in TEvent> where TEvent : class
{
    /// <summary>
    /// Publishes a message to all subscribers of its configured channel.
    /// </summary>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask PublishAsync(TEvent @event, CancellationToken cancellationToken = default);
}