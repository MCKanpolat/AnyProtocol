using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AnyProtocol.Storage.Abstraction;
using StackExchange.Redis;

namespace AnyProtocol.Storage.Redis;

/// <summary>Stores bounded, short-lived serialized message bodies in Redis.</summary>
public sealed class RedisLargePayloadStore : ILargePayloadStore, IAsyncDisposable
{
    private const string StoreScript = """
        local current = redis.call('GET', KEYS[1])
        if current then
            if string.sub(current, 1, 64) == ARGV[1] then
                return current
            end
            return redis.error_reply('PAYLOAD_ID_CONFLICT')
        end
        local value = ARGV[1] .. '\n' .. ARGV[2] .. '\n' .. ARGV[3]
        redis.call('PSETEX', KEYS[1], ARGV[4], value)
        return value
        """;

    private readonly RedisPayloadStoreOptions _options;
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a Redis payload store without opening a connection.</summary>
    /// <param name="name">The logical provider name.</param>
    /// <param name="options">The validated Redis options.</param>
    /// <param name="timeProvider">The optional clock used for deterministic testing.</param>
    public RedisLargePayloadStore(
        string name,
        RedisPayloadStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        Name = name;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connection = new Lazy<Task<ConnectionMultiplexer>>(
            () => ConnectionMultiplexer.ConnectAsync(_options.ConnectionString!),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public async ValueTask<StoredPayloadReference> StoreAsync(
        string messageId,
        ReadOnlyMemory<byte> payload,
        PayloadWriteOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(options);
        if (options.TimeToLive <= TimeSpan.Zero ||
            options.ExpectedLength != payload.Length ||
            string.IsNullOrWhiteSpace(options.Sha256))
        {
            throw new ArgumentException("Payload write metadata is invalid.", nameof(options));
        }

        var expiresAt = _timeProvider.GetUtcNow().Add(options.TimeToLive);
        var key = CreateKey(messageId);
        try
        {
            var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var result = await database.ScriptEvaluateAsync(
                    StoreScript,
                    [key],
                    [
                        options.Sha256.ToLowerInvariant(),
                        expiresAt.ToUnixTimeMilliseconds(),
                        payload.ToArray(),
                        checked((long)Math.Ceiling(options.TimeToLive.TotalMilliseconds))
                    ])
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var stored = (byte[]?)result ??
                         throw Unavailable("Redis returned no result for a payload write.");
            var parsed = ParseStoredValue(stored);
            return new StoredPayloadReference(
                Name,
                key,
                parsed.Payload.Length,
                parsed.Digest,
                parsed.ExpiresAt);
        }
        catch (RedisServerException exception) when (
            exception.Message.Contains("PAYLOAD_ID_CONFLICT", StringComparison.Ordinal))
        {
            throw new LargePayloadException(
                LargePayloadFailureCodes.IdConflict,
                "The message ID is already associated with different payload content.",
                innerException: exception);
        }
        catch (RedisException exception)
        {
            throw Unavailable("Redis could not store the large payload.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> LoadAsync(
        StoredPayloadReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        try
        {
            var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var value = await database.StringGetAsync(reference.Key)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!value.HasValue)
            {
                throw new LargePayloadException(
                    LargePayloadFailureCodes.NotFound,
                    "The referenced payload is missing or expired.");
            }

            return ParseStoredValue((byte[])value!).Payload;
        }
        catch (LargePayloadException)
        {
            throw;
        }
        catch (RedisException exception)
        {
            throw Unavailable("Redis could not load the large payload.", exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(
        StoredPayloadReference reference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(reference);
        try
        {
            var database = await GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
            _ = await database.KeyDeleteAsync(reference.Key)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RedisException exception)
        {
            throw Unavailable("Redis could not delete the large payload.", exception);
        }
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

    private string CreateKey(string messageId)
    {
        var idHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(messageId)))
            .ToLowerInvariant();
        return $"{_options.KeyPrefix}:{idHash}";
    }

    private void ValidateReference(StoredPayloadReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.StoreName, Name, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(reference.Key))
        {
            throw new LargePayloadException(
                LargePayloadFailureCodes.InvalidReference,
                "The stored payload reference does not belong to this provider.");
        }
    }

    private static ParsedPayload ParseStoredValue(ReadOnlySpan<byte> value)
    {
        var firstSeparator = value.IndexOf((byte)'\n');
        var secondRelative = firstSeparator < 0
            ? -1
            : value[(firstSeparator + 1)..].IndexOf((byte)'\n');
        var secondSeparator = secondRelative < 0 ? -1 : firstSeparator + 1 + secondRelative;
        if (firstSeparator != 64 || secondSeparator <= firstSeparator + 1 ||
            !long.TryParse(
                Encoding.ASCII.GetString(value[(firstSeparator + 1)..secondSeparator]),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var expiryMilliseconds))
        {
            throw new LargePayloadException(
                LargePayloadFailureCodes.IntegrityFailed,
                "The Redis payload record is malformed.");
        }

        return new ParsedPayload(
            Encoding.ASCII.GetString(value[..firstSeparator]),
            DateTimeOffset.FromUnixTimeMilliseconds(expiryMilliseconds),
            value[(secondSeparator + 1)..].ToArray());
    }

    private static LargePayloadException Unavailable(string message, Exception? inner = null)
        => new(
            LargePayloadFailureCodes.StoreUnavailable,
            message,
            retryable: true,
            inner);

    private sealed record ParsedPayload(
        string Digest,
        DateTimeOffset ExpiresAt,
        ReadOnlyMemory<byte> Payload);
}
