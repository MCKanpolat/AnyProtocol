namespace AnyProtocol.Protocol.ZeroMq;

/// <summary>
/// Identifies the available zero mq role values.
/// </summary>
public enum ZeroMqRole
{
    /// <summary>
    /// Configures zero mq protocol behavior.
    /// </summary>
    Client,
    /// <summary>
    /// Configures zero mq protocol behavior.
    /// </summary>
    Server
}

/// <summary>
/// Configures zero mq protocol behavior.
/// </summary>
public sealed class ZeroMqProtocolOptions
{
    /// <summary>
    /// Gets or initializes the role.
    /// </summary>
    /// <value>The role.</value>
    public required ZeroMqRole Role { get; init; }

    /// <summary>
    /// Gets or initializes the router endpoint.
    /// </summary>
    /// <value>The router endpoint.</value>
    public required string RouterEndpoint { get; init; }

    /// <summary>
    /// Gets or initializes the publisher endpoint.
    /// </summary>
    /// <value>The publisher endpoint.</value>
    public required string PublisherEndpoint { get; init; }

    /// <summary>
    /// Gets or initializes the client identity.
    /// </summary>
    /// <value>The client identity.</value>
    public string? ClientIdentity { get; init; }

    /// <summary>
    /// Gets or initializes the high watermark.
    /// </summary>
    /// <value>The high watermark.</value>
    public int HighWatermark { get; init; } = 1_000;

    /// <summary>
    /// Gets or initializes the maximum messages queued for each local subscription.
    /// </summary>
    /// <value>The capacity. Socket processing waits when the queue is full.</value>
    public int SubscriptionQueueCapacity { get; init; } = 1_000;

    /// <summary>
    /// Gets or initializes the poll interval.
    /// </summary>
    /// <value>The poll interval.</value>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(2);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(RouterEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(PublisherEndpoint);
        if (HighWatermark <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HighWatermark),
                HighWatermark,
                "HighWatermark must be greater than zero.");
        }

        if (SubscriptionQueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SubscriptionQueueCapacity),
                SubscriptionQueueCapacity,
                "SubscriptionQueueCapacity must be greater than zero.");
        }

        if (PollInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollInterval),
                PollInterval,
                "PollInterval cannot be negative.");
        }
    }
}
