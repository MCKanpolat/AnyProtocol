namespace AnyProtocol.Protocol.Abstraction;

/// <summary>Describes when a transport considers a send complete.</summary>
public enum TransportDeliveryGuarantee
{
    /// <summary>
    /// Identifies the available transport ordering values.
    /// </summary>
    AtMostOnce,
    /// <summary>
    /// Identifies the available transport ordering values.
    /// </summary>
    AtLeastOnce
}

/// <summary>Describes the ordering boundary guaranteed by a transport.</summary>
public enum TransportOrdering
{
    /// <summary>
    /// Identifies the available transport durability values.
    /// </summary>
    None,
    /// <summary>
    /// Identifies the available transport durability values.
    /// </summary>
    PerChannel,
    /// <summary>
    /// Identifies the available transport durability values.
    /// </summary>
    PerPartition
}

/// <summary>Describes whether accepted messages survive process or broker restarts.</summary>
public enum TransportDurability
{
    /// <summary>
    /// Describes the delivery and ordering guarantees of a messaging transport.
    /// </summary>
    Volatile,
    /// <summary>
    /// Describes the delivery and ordering guarantees of a messaging transport.
    /// </summary>
    Durable
}

/// <summary>
/// Defines the observable delivery and flow-control behavior of a messaging transport.
/// </summary>
public sealed record TransportSemantics
{
    /// <summary>
    /// Gets or initializes the delivery guarantee.
    /// </summary>
    /// <value>The delivery guarantee.</value>
    public required TransportDeliveryGuarantee DeliveryGuarantee { get; init; }

    /// <summary>
    /// Gets or initializes the ordering.
    /// </summary>
    /// <value>The ordering.</value>
    public required TransportOrdering Ordering { get; init; }

    /// <summary>
    /// Gets or initializes the durability.
    /// </summary>
    /// <value>The durability.</value>
    public required TransportDurability Durability { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports publish subscribe applies.
    /// </summary>
    /// <value>true when supports publish subscribe applies; otherwise, false.</value>
    public bool SupportsPublishSubscribe { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports competing consumers applies.
    /// </summary>
    /// <value>true when supports competing consumers applies; otherwise, false.</value>
    public bool SupportsCompetingConsumers { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports native request reply applies.
    /// </summary>
    /// <value>true when supports native request reply applies; otherwise, false.</value>
    public bool SupportsNativeRequestReply { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports native streaming applies.
    /// </summary>
    /// <value>true when supports native streaming applies; otherwise, false.</value>
    public bool SupportsNativeStreaming { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports partitioning applies.
    /// </summary>
    /// <value>true when supports partitioning applies; otherwise, false.</value>
    public bool SupportsPartitioning { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports backpressure applies.
    /// </summary>
    /// <value>true when supports backpressure applies; otherwise, false.</value>
    public bool SupportsBackpressure { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether supports cancellation applies.
    /// </summary>
    /// <value>true when supports cancellation applies; otherwise, false.</value>
    public bool SupportsCancellation { get; init; }

    /// <summary>
    /// Creates conservative semantics for transports that only expose legacy capabilities.
    /// </summary>
    public static TransportSemantics FromCapabilities(TransportCapabilities capabilities)
        => new()
        {
            DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
            Ordering = TransportOrdering.None,
            Durability = TransportDurability.Volatile,
            SupportsPublishSubscribe =
                capabilities.HasFlag(TransportCapabilities.PublishSubscribe),
            SupportsCompetingConsumers =
                capabilities.HasFlag(TransportCapabilities.CompetingConsumers),
            SupportsNativeRequestReply =
                capabilities.HasFlag(TransportCapabilities.NativeRequestReply),
            SupportsNativeStreaming =
                capabilities.HasFlag(TransportCapabilities.NativeStreaming),
            SupportsBackpressure =
                capabilities.HasFlag(TransportCapabilities.NativeStreaming),
            SupportsCancellation = true
        };
}
