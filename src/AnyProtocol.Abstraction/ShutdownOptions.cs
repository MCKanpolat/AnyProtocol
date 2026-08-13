namespace AnyProtocol.Abstraction;

/// <summary>
/// Configures bounded AnyProtocol shutdown draining.
/// </summary>
public sealed record ShutdownOptions
{
    /// <summary>
    /// The default time allowed for admitted handlers to complete.
    /// </summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The default time allowed for forced cancellation cleanup.
    /// </summary>
    public static readonly TimeSpan DefaultForcedCancellationTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or initializes the time allowed for admitted work to drain.
    /// </summary>
    public TimeSpan DrainTimeout { get; init; } = DefaultDrainTimeout;

    /// <summary>
    /// Gets or initializes the time allowed for forced-cancellation cleanup.
    /// </summary>
    public TimeSpan ForcedCancellationTimeout { get; init; } = DefaultForcedCancellationTimeout;

    /// <summary>
    /// Validates the shutdown policy.
    /// </summary>
    public void Validate()
    {
        if (DrainTimeout < TimeSpan.Zero || DrainTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(DrainTimeout), "DrainTimeout must be finite and non-negative.");
        }

        if (ForcedCancellationTimeout < TimeSpan.Zero ||
            ForcedCancellationTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ForcedCancellationTimeout),
                "ForcedCancellationTimeout must be finite and non-negative.");
        }
    }
}
