using AnyProtocol.Storage.Abstraction;
using AnyProtocol.Storage.Abstraction.Exceptions;
using AnyProtocol.Storage.Redis.Configuration;

namespace AnyProtocol.Storage.Redis;

/// <summary>
/// Provides the redis message storage implementation used by AnyProtocol applications.
/// </summary>
public sealed class RedisMessageStorage : IMessageStorage
{
    private readonly IRedisConnection _redisConnection;
    private readonly RedisConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the RedisMessageStorage class.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="redisConnection">The redis connection.</param>
    public RedisMessageStorage(RedisConfiguration configuration, IRedisConnection redisConnection)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _redisConnection = redisConnection;
    }

    /// <summary>
    /// Gets async.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the get async.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var value = await _redisConnection.Database.StringGetAsync(key);

        if (value.HasValue)
        {
            return value;
        }

        throw new MessageNotFoundException(key);
    }

    /// <summary>
    /// Performs the set async operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        _ = await _redisConnection.Database.StringSetAsync(key, bytes, TimeSpan.FromSeconds(_configuration.ExpiryInSeconds));
    }

    /// <summary>
    /// Releases resources owned by this instance.
    /// </summary>
    public void Dispose()
    {

    }
}