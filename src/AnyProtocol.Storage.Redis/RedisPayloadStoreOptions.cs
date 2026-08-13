namespace AnyProtocol.Storage.Redis;

/// <summary>Configures a Redis large-payload store.</summary>
public sealed class RedisPayloadStoreOptions
{
    /// <summary>Gets or sets the StackExchange.Redis connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets the required application/environment key namespace.</summary>
    public string? KeyPrefix { get; set; }
}
