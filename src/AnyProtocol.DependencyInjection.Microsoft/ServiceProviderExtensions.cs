using AnyProtocol.Configuration;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Services;
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

        var runtimePlan = builder.Build(descriptorFactory);

        services.AddSingleton(runtimePlan);
        services.AddSingleton(runtimePlan.Serializer);
        services.AddSingleton(descriptorFactory);
        services.TryAddSingleton<IMessageIdGenerator, DefaultMessageIdGenerator>();
        services.TryAddSingleton<IDateTimeProvider, DefaultDateTimeProvider>();
        services.TryAddSingleton<IMessageEnvelopeFactory, DefaultMessageEnvelopeFactory>();
        services.TryAddSingleton<AnyProtocol.Logging.Abstraction.ILogWriterFactory>(
            AnyProtocol.Logging.Abstraction.NullLogWriterFactory.Instance);
        services.TryAddSingleton<IRequestAdmission, RequestAdmissionCoordinator>();
        services.TryAddSingleton<OutboundOperationLifetime>();
        services.TryAddSingleton(new ShutdownOptions());
        services.TryAddSingleton<LargePayloadStoreRegistry>();
        services.AddSingleton(
            provider => new LargePayloadOffloader(
                runtimePlan.LargePayloadOffload,
                provider.GetRequiredService<LargePayloadStoreRegistry>()));
        services.AddSingleton(
            provider => new LargePayloadMaterializer(
                provider.GetRequiredService<LargePayloadStoreRegistry>(),
                provider.GetRequiredService<IDateTimeProvider>()));
        services.AddSingleton(
            new TransportRegistry(runtimePlan.RegisteredTransports));
        services.TryAddSingleton<ProtocolExposureRegistry>();
        services.AddSingleton<IDependencyResolverFactory, MicrosoftDependencyResolverFactory>();
        services.AddSingleton<IContractProxyFactory, GeneratedContractProxyFactory>();
        services.AddSingleton(
            provider => new OutboundOperationExecutor(
                provider.GetRequiredService<IDependencyResolverFactory>(),
                provider.GetRequiredService<IRequestAdmission>(),
                runtimePlan.RegisteredClientFilters,
                provider.GetRequiredService<OutboundOperationLifetime>()));
        services.AddSingleton(
            provider => new MessageDispatcher(
                provider.GetRequiredService<IDependencyResolverFactory>(),
                provider.GetRequiredService<IMessageSerializer>(),
                runtimePlan.RegisteredServerFilters,
                provider.GetServices<IErrorHandler>(),
                provider.GetRequiredService<IMessageEnvelopeFactory>(),
                provider.GetRequiredService<AnyProtocol.Logging.Abstraction.ILogWriterFactory>(),
                provider.GetRequiredService<LargePayloadMaterializer>(),
                provider.GetRequiredService<LargePayloadOffloader>()));
        services.AddSingleton(
            provider => new AnyProtocolClientInvoker(
                provider.GetRequiredService<TransportRegistry>(),
                provider.GetRequiredService<IMessageSerializer>(),
                runtimePlan.ClientRegistrations,
                provider.GetRequiredService<OutboundOperationExecutor>(),
                provider.GetRequiredService<IMessageEnvelopeFactory>(),
                provider.GetRequiredService<LargePayloadOffloader>(),
                provider.GetRequiredService<LargePayloadMaterializer>()));
        services.AddSingleton<IClientInvoker>(
            provider => provider.GetRequiredService<AnyProtocolClientInvoker>());
        services.AddSingleton<RuntimeLifetimeOwner>();
        services.AddSingleton(
            provider => new AnyProtocolBus(
                runtimePlan,
                provider.GetRequiredService<TransportRegistry>(),
                provider.GetRequiredService<MessageDispatcher>(),
                provider.GetRequiredService<IRequestAdmission>(),
                provider.GetRequiredService<ShutdownOptions>(),
                provider.GetRequiredService<AnyProtocol.Logging.Abstraction.ILogWriterFactory>(),
                provider.GetRequiredService<OutboundOperationLifetime>()));
        services.AddSingleton<IAnyProtocolBus>(
            provider => provider.GetRequiredService<AnyProtocolBus>());
        services.AddHostedService<AuthorizationConfigurationValidationService>();
        services.AddHostedService<InboxConfigurationValidationService>();
        services.AddHostedService<ProtocolExposureValidationService>();
        services.AddHostedService<AnyProtocolHostedService>();

        foreach (var server in runtimePlan.ServerRegistrations)
        {
            services.AddScoped(server.ImplementationType);
        }

        foreach (var client in runtimePlan.ClientRegistrations)
        {
            services.AddSingleton(
                client.ContractType,
                provider => provider.GetRequiredService<IContractProxyFactory>()
                    .Create(client.ContractType, provider.GetRequiredService<IClientInvoker>()));
        }

        foreach (var eventRegistration in runtimePlan.EventRegistrations)
        {
            services.AddScoped(eventRegistration.HandlerType);
            var eventTransport = runtimePlan.RegisteredTransports[eventRegistration.Protocol];
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
                    eventRegistration.TransportName,
                    provider.GetRequiredService<OutboundOperationExecutor>(),
                    provider.GetRequiredService<IMessageEnvelopeFactory>(),
                    provider.GetRequiredService<LargePayloadOffloader>())!);
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
