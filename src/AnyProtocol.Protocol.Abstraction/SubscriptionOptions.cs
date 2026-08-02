namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Configures how a message subscription consumes and groups messages.
/// </summary>
public sealed record SubscriptionOptions
{
    /// <summary>
    /// Gets or initializes the consumer group.
    /// </summary>
    /// <value>The consumer group.</value>
    public string? ConsumerGroup { get; init; }

    /// <summary>
    /// Gets or initializes the max concurrency.
    /// </summary>
    /// <value>The max concurrency.</value>
    public int MaxConcurrency { get; init; } = 1;
}
