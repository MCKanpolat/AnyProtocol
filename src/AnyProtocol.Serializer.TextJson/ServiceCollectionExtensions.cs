using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol.Serializer.TextJson;

/// <summary>
/// Provides extension methods for service collection configuration and registration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds text json serializer support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add text json serializer operation.</returns>
    public static IDependencyService AddTextJsonSerializer(this IDependencyService services)
    {
        services.AddTransient<IMessageSerializer, TextJsonMessageSerializer>();
        return services;
    }
}