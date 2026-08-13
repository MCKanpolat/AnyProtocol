using AnyProtocol.Storage.Abstraction;
using Microsoft.Extensions.DependencyInjection;

namespace AnyProtocol.Storage.Redis;

/// <summary>Registers named Redis payload stores with Microsoft DI.</summary>
public static class RedisPayloadStoreServiceCollectionExtensions
{
    /// <summary>Registers one named Redis large-payload provider.</summary>
    public static IServiceCollection AddAnyProtocolRedisPayloadStore(
        this IServiceCollection services,
        string name,
        Action<RedisPayloadStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisPayloadStoreOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        services.AddSingleton<ILargePayloadStore>(
            _ => new RedisLargePayloadStore(name, options));
        return services;
    }

    /// <summary>Registers one named Redis inbox/deduplication provider.</summary>
    public static IServiceCollection AddAnyProtocolRedisDeduplicationStore(
        this IServiceCollection services,
        string name,
        Action<RedisMessageDeduplicationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RedisMessageDeduplicationOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KeyPrefix);
        services.AddSingleton<IMessageDeduplicationStore>(
            _ => new RedisMessageDeduplicationStore(name, options));
        return services;
    }
}
