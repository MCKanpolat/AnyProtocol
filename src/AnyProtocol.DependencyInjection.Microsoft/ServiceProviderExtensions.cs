using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AnyProtocol.DependencyInjection.Microsoft;

/// <summary>
/// Provides extension methods for service provider configuration and registration.
/// </summary>
public static class ServiceProviderExtensions
{
    /// <summary>
    /// Adds anyprotocol support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">The configure.</param>
    /// <returns>The result of the add anyprotocol operation.</returns>
    public static IServiceCollection AddAnyProtocol(
        this IServiceCollection services,
        Action<LinkBuilder> configure)
        => AddAnyProtocolCore(services, configure, null);

    /// <summary>
    /// Registers AnyProtocol and overlays named client/server protocol selections from configuration.
    /// </summary>
    public static IServiceCollection AddAnyProtocol(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LinkBuilder> configure)
        => AddAnyProtocolCore(services, configure, configuration);

    private static IServiceCollection AddAnyProtocolCore(
        IServiceCollection services,
        Action<LinkBuilder> configure,
        IConfiguration? protocolConfiguration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var descriptorFactory = new ContractDescriptorFactory();
        var builder = new LinkBuilder();
        configure(builder);
        if (protocolConfiguration is not null)
        {
            ApplyProtocolConfiguration(builder, protocolConfiguration);
        }

        var configuration = builder.Build(descriptorFactory);

        services.AddSingleton(configuration);
        services.AddSingleton(configuration.Serializer!);
        services.AddSingleton(descriptorFactory);
        services.AddSingleton(
            new TransportRegistry(configuration.RegisteredTransports));
        services.TryAddSingleton<ProtocolExposureRegistry>();
        services.AddSingleton<IDependencyResolverFactory, MicrosoftDependencyResolverFactory>();
        services.AddSingleton<IContractProxyFactory, GeneratedContractProxyFactory>();
        services.AddSingleton(
            provider => new MessageDispatcher(
                provider.GetRequiredService<IDependencyResolverFactory>(),
                provider.GetRequiredService<IMessageSerializer>(),
                configuration.RegisteredServerFilters,
                provider.GetServices<IErrorHandler>()));
        services.AddSingleton(
            provider => new AnyProtocolClientInvoker(
                provider.GetRequiredService<TransportRegistry>(),
                provider.GetRequiredService<IMessageSerializer>(),
                configuration.ClientRegistrations,
                configuration.RegisteredClientFilters,
                provider.GetRequiredService<IDependencyResolverFactory>().CreateResolver()));
        services.AddSingleton<IClientInvoker>(
            provider => provider.GetRequiredService<AnyProtocolClientInvoker>());
        services.AddSingleton(
            provider => new AnyProtocolBus(
                configuration,
                descriptorFactory,
                provider.GetRequiredService<TransportRegistry>(),
                provider.GetRequiredService<MessageDispatcher>()));
        services.AddSingleton<IAnyProtocolBus>(
            provider => provider.GetRequiredService<AnyProtocolBus>());
        services.AddHostedService<ProtocolExposureValidationService>();
        services.AddHostedService<AnyProtocolHostedService>();

        foreach (var server in configuration.ServerRegistrations)
        {
            services.AddScoped(server.ImplementationType);
        }

        foreach (var client in configuration.ClientRegistrations)
        {
            services.AddSingleton(
                client.ContractType,
                provider => provider.GetRequiredService<IContractProxyFactory>()
                    .Create(client.ContractType, provider.GetRequiredService<IClientInvoker>()));
        }

        foreach (var eventRegistration in configuration.EventRegistrations)
        {
            services.AddScoped(eventRegistration.HandlerType);
            var eventTransport = configuration.RegisteredTransports[eventRegistration.Protocol];
            if (eventTransport is not ISendTransport)
            {
                // Native server transports receive events through their host endpoint and
                // intentionally do not expose a publisher capability.
                continue;
            }

            var publisherService = typeof(IEventPublisher<>).MakeGenericType(eventRegistration.EventType);
            var publisherType = typeof(EventPublisher<>).MakeGenericType(eventRegistration.EventType);
            services.AddSingleton(
                publisherService,
                provider => Activator.CreateInstance(
                    publisherType,
                    provider.GetRequiredService<TransportRegistry>()
                        .GetRequired(eventRegistration.TransportName) as ISendTransport,
                    provider.GetRequiredService<IMessageSerializer>(),
                    eventRegistration.Channel,
                    eventRegistration.TransportName)!);
        }

        return services;
    }

    private static void ApplyProtocolConfiguration(
        LinkBuilder builder,
        IConfiguration configuration)
    {
        foreach (var client in configuration.GetSection("Clients").GetChildren())
        {
            var value = client["Protocol"];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Client registration '{client.Key}' must configure one non-empty Protocol value.");
            }

            builder.ConfigureClientProtocol(client.Key, ProtocolKey.Create(value));
        }

        foreach (var server in configuration.GetSection("Servers").GetChildren())
        {
            var protocolSection = server.GetSection("Protocols");
            var values = protocolSection.GetChildren()
                .Select(static child => child.Value)
                .ToArray();
            if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Server registration '{server.Key}' must configure at least one protocol.");
            }

            builder.ConfigureServerProtocols(
                server.Key,
                values.Select(value => ProtocolKey.Create(value!)).ToArray());
        }
    }
}
