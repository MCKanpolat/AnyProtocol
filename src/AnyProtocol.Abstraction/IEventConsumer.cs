namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for event consumer.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventConsumer<in TEvent> where TEvent : class
{
    /// <summary>
    /// Performs the consume async operation.
    /// </summary>
    /// <param name="e">The e.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask ConsumeAsync(TEvent e);
}