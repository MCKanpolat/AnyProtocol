using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.InMemory;

/// <summary>
/// Configures in memory protocol behavior.
/// </summary>
public sealed record InMemoryProtocolOptions
{
    /// <summary>
    /// Gets or initializes the maximum messages queued for each subscription.
    /// </summary>
    /// <value>The capacity. Producers wait when the queue is full.</value>
    public int SubscriptionQueueCapacity { get; init; } = 1_000;

    /// <summary>
    /// Gets or initializes the delivery delay.
    /// </summary>
    /// <value>The delivery delay.</value>
    public TimeSpan DeliveryDelay { get; init; }

    /// <summary>
    /// Gets or initializes the fault injector.
    /// </summary>
    /// <value>The fault injector.</value>
    public Func<string, TransportEnvelope, Exception?>? FaultInjector { get; init; }
}
