namespace AnyProtocol.Storage.Abstraction;

/// <summary>Coordinates durable inbox claims for at-least-once message delivery.</summary>
public interface IMessageDeduplicationStore
{
    /// <summary>Gets the logical provider registration name.</summary>
    string Name { get; }

    /// <summary>
    /// Atomically acquires a processing lease, or returns <see langword="null"/> when the
    /// message is already being processed or has completed within its retention window.
    /// </summary>
    ValueTask<MessageDeduplicationLease?> TryAcquireAsync(
        string messageId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a successfully processed lease as complete for the retention window.</summary>
    ValueTask CompleteAsync(
        MessageDeduplicationLease lease,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a failed processing lease so that the broker can redeliver it.</summary>
    ValueTask ReleaseAsync(
        MessageDeduplicationLease lease,
        CancellationToken cancellationToken = default);
}

/// <summary>Identifies ownership of one in-flight inbox record.</summary>
/// <param name="MessageId">The transport message identifier.</param>
/// <param name="Token">The opaque ownership token issued by the store.</param>
public readonly record struct MessageDeduplicationLease(string MessageId, string Token);
