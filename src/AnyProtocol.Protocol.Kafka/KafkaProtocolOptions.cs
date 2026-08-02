namespace AnyProtocol.Protocol.Kafka;

/// <summary>
/// Configures kafka protocol behavior.
/// </summary>
public sealed record KafkaProtocolOptions
{
    /// <summary>
    /// Gets or initializes the bootstrap servers.
    /// </summary>
    /// <value>The bootstrap servers.</value>
    public required string BootstrapServers { get; init; }

    /// <summary>
    /// Gets or initializes the client id.
    /// </summary>
    /// <value>The client id.</value>
    public string ClientId { get; init; } = $"anyprotocol-{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or initializes the topic prefix.
    /// </summary>
    /// <value>The topic prefix.</value>
    public string? TopicPrefix { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether auto create topics applies.
    /// </summary>
    /// <value>true when auto create topics applies; otherwise, false.</value>
    public bool AutoCreateTopics { get; init; } = true;

    /// <summary>
    /// Gets or initializes the topic partitions.
    /// </summary>
    /// <value>The topic partitions.</value>
    public int TopicPartitions { get; init; } = 3;

    /// <summary>
    /// Gets or initializes the topic replication factor.
    /// </summary>
    /// <value>The topic replication factor.</value>
    public short TopicReplicationFactor { get; init; } = 1;

    /// <summary>
    /// Gets or initializes the reply topic retention.
    /// </summary>
    /// <value>The reply topic retention.</value>
    public TimeSpan ReplyTopicRetention { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or initializes the dead letter suffix.
    /// </summary>
    /// <value>The dead letter suffix.</value>
    public string DeadLetterSuffix { get; init; } = ".dead-letter";

    /// <summary>
    /// Gets or initializes a value indicating whether enable dead letter applies.
    /// </summary>
    /// <value>true when enable dead letter applies; otherwise, false.</value>
    public bool EnableDeadLetter { get; init; } = true;

    /// <summary>
    /// Gets or initializes the subscription startup timeout.
    /// </summary>
    /// <value>The subscription startup timeout.</value>
    public TimeSpan SubscriptionStartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or initializes the consumer poll interval.
    /// </summary>
    /// <value>The consumer poll interval.</value>
    public TimeSpan ConsumerPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Gets or initializes the producer flush timeout.
    /// </summary>
    /// <value>The producer flush timeout.</value>
    public TimeSpan ProducerFlushTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or initializes the producer config.
    /// </summary>
    /// <value>The producer config.</value>
    public IReadOnlyDictionary<string, string> ProducerConfig { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or initializes the consumer config.
    /// </summary>
    /// <value>The consumer config.</value>
    public IReadOnlyDictionary<string, string> ConsumerConfig { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
