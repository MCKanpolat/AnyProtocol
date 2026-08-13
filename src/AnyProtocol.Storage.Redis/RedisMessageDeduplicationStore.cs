using System.Security.Cryptography;
using System.Text;
using AnyProtocol.Storage.Abstraction;
using StackExchange.Redis;

namespace AnyProtocol.Storage.Redis;

/// <summary>Coordinates durable inbox claims through atomic Redis operations.</summary>
public sealed class RedisMessageDeduplicationStore : IMessageDeduplicationStore, IAsyncDisposable
{
    private const string CompleteScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            redis.call('PSETEX', KEYS[1], ARGV[2], 'completed')
            return 1
        end
        return 0
        """;

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    private readonly RedisMessageDeduplicationOptions _options;
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;

    /// <summary>Creates a Redis inbox store without opening a connection.</summary>
    public RedisMessageDeduplicationStore(
        string name,
        RedisMessageDeduplicationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        Name = name;
        _options = options;
        _connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectionMultiplexer.ConnectAsync(_options.ConnectionString!),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<MessageDeduplicationLease?> TryAcquireAsync(
        string messageId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        var lease = new MessageDeduplicationLease(
            messageId,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
        var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var acquired = await database.StringSetAsync(
                CreateKey(messageId),
                lease.Token,
                leaseDuration,
                When.NotExists)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return acquired ? lease : null;
    }

    /// <inheritdoc />
    public async ValueTask CompleteAsync(
        MessageDeduplicationLease lease,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var result = await database.ScriptEvaluateAsync(
                CompleteScript,
                [CreateKey(lease.MessageId)],
                [lease.Token, checked((long)Math.Ceiling(retention.TotalMilliseconds))])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        if ((long)result != 1)
        {
            throw new InvalidOperationException(
                $"The inbox lease for message '{lease.MessageId}' is no longer owned.");
        }
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(
        MessageDeduplicationLease lease,
        CancellationToken cancellationToken = default)
    {
        var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
        _ = await database.ScriptEvaluateAsync(
                ReleaseScript,
                [CreateKey(lease.MessageId)],
                [lease.Token])
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (!_connection.IsValueCreated)
        {
            return;
        }

        var connection = await _connection.Value.ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private async ValueTask<IDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        var connection = await _connection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return connection.GetDatabase();
    }

    private RedisKey CreateKey(string messageId)
    {
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))
            .ToLowerInvariant();
        return $"{_options.KeyPrefix}:{idHash}";
    }
}
