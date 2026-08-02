namespace AnyProtocol.Storage.Redis.Configuration;

/// <summary>
/// Provides the redis configuration implementation used by AnyProtocol applications.
/// </summary>
public record RedisConfiguration
{
    /// <summary>
    /// Gets or initializes the connection string.
    /// </summary>
    /// <value>The connection string.</value>
    public string? ConnectionString { get; set; }
    /// <summary>
    /// Gets or initializes the expiry in seconds.
    /// </summary>
    /// <value>The expiry in seconds.</value>
    public int ExpiryInSeconds { get; set; } = 300;
}