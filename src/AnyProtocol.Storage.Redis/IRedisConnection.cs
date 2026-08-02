using StackExchange.Redis;

namespace AnyProtocol.Storage.Redis;

/// <summary>
/// Defines operations for redis connection.
/// </summary>
public interface IRedisConnection
{
    /// <summary>
    /// Gets the database.
    /// </summary>
    /// <value>The database.</value>
    IDatabase Database { get; }
}