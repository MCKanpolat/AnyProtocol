using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol.Storage.Redis;

/// <summary>
/// Provides extension methods for service collection configuration and registration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds redis storage support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add redis storage operation.</returns>
    public static IDependencyService AddRedisStorage(this IDependencyService services)
    {
        services.AddTransient<IMessageStorage, RedisMessageStorage>();

        return services;
    }
}