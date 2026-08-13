using System.Collections.Concurrent;
using System.Security.Cryptography;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>Provides a process-local inbox store for tests and single-instance applications.</summary>
public sealed class InMemoryMessageDeduplicationStore : IMessageDeduplicationStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a process-local inbox store.</summary>
    public InMemoryMessageDeduplicationStore(
        string name = "in-memory",
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ValueTask<MessageDeduplicationLease?> TryAcquireAsync(
        string messageId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            var now = _timeProvider.GetUtcNow();
            if (_entries.TryGetValue(messageId, out var current))
            {
                if (current.ExpiresAt > now)
                {
                    return ValueTask.FromResult<MessageDeduplicationLease?>(null);
                }

                _ = ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(
                    new KeyValuePair<string, Entry>(messageId, current));
                continue;
            }

            var lease = new MessageDeduplicationLease(
                messageId,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            if (_entries.TryAdd(
                    messageId,
                    new Entry(lease.Token, now.Add(leaseDuration), Completed: false)))
            {
                return ValueTask.FromResult<MessageDeduplicationLease?>(lease);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(
        MessageDeduplicationLease lease,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        cancellationToken.ThrowIfCancellationRequested();
        while (_entries.TryGetValue(lease.MessageId, out var current))
        {
            if (!string.Equals(current.Token, lease.Token, StringComparison.Ordinal) ||
                current.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                break;
            }

            var completed = current with
            {
                ExpiresAt = _timeProvider.GetUtcNow().Add(retention),
                Completed = true
            };
            if (_entries.TryUpdate(lease.MessageId, completed, current))
            {
                return ValueTask.CompletedTask;
            }
        }

        return ValueTask.FromException(
            new InvalidOperationException(
                $"The inbox lease for message '{lease.MessageId}' is no longer owned."));
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(
        MessageDeduplicationLease lease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.TryGetValue(lease.MessageId, out var current) &&
            !current.Completed &&
            string.Equals(current.Token, lease.Token, StringComparison.Ordinal))
        {
            _ = ((ICollection<KeyValuePair<string, Entry>>)_entries).Remove(
                new KeyValuePair<string, Entry>(lease.MessageId, current));
        }

        return ValueTask.CompletedTask;
    }

    private sealed record Entry(string Token, DateTimeOffset ExpiresAt, bool Completed);
}
