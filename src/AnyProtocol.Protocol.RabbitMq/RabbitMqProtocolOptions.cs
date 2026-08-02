namespace AnyProtocol.Protocol.RabbitMq;

/// <summary>
/// Configures rabbit mq protocol behavior.
/// </summary>
public sealed record RabbitMqProtocolOptions
{
    /// <summary>
    /// Gets or initializes the connection uri.
    /// </summary>
    /// <value>The connection uri.</value>
    public required Uri ConnectionUri { get; init; }

    /// <summary>
    /// Gets or initializes the client provided name.
    /// </summary>
    /// <value>The client provided name.</value>
    public string ClientProvidedName { get; init; } = $"anyprotocol-{Guid.NewGuid():N}";

    /// <summary>
    /// Gets or initializes the exchange name.
    /// </summary>
    /// <value>The exchange name.</value>
    public string ExchangeName { get; init; } = "anyprotocol";

    /// <summary>
    /// Gets or initializes the routing key prefix.
    /// </summary>
    /// <value>The routing key prefix.</value>
    public string? RoutingKeyPrefix { get; init; }

    /// <summary>
    /// Gets or initializes the prefetch count.
    /// </summary>
    /// <value>The prefetch count.</value>
    public ushort PrefetchCount { get; init; } = 32;

    /// <summary>
    /// Gets or initializes the max delivery attempts.
    /// </summary>
    /// <value>The max delivery attempts.</value>
    public int MaxDeliveryAttempts { get; init; } = 5;

    /// <summary>
    /// Gets or initializes the should retry handler exception.
    /// </summary>
    /// <value>The should retry handler exception.</value>
    public Func<Exception, bool> ShouldRetryHandlerException { get; init; } =
        static exception => exception is not OperationCanceledException;

    /// <summary>
    /// Gets or initializes a value indicating whether enable dead letter applies.
    /// </summary>
    /// <value>true when enable dead letter applies; otherwise, false.</value>
    public bool EnableDeadLetter { get; init; } = true;

    /// <summary>
    /// Gets or initializes the dead letter suffix.
    /// </summary>
    /// <value>The dead letter suffix.</value>
    public string DeadLetterSuffix { get; init; } = ".dead-letter";

    /// <summary>
    /// Gets or initializes the confirm timeout.
    /// </summary>
    /// <value>The confirm timeout.</value>
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or initializes the readiness timeout.
    /// </summary>
    /// <value>The readiness timeout.</value>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets or initializes the network recovery interval.
    /// </summary>
    /// <value>The network recovery interval.</value>
    public TimeSpan NetworkRecoveryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or initializes the shutdown timeout.
    /// </summary>
    /// <value>The shutdown timeout.</value>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(10);
}
