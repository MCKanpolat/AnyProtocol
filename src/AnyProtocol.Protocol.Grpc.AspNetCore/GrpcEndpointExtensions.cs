using AnyProtocol.Configuration;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AnyProtocol.Protocol.Grpc.AspNetCore;

/// <summary>
/// Provides extension methods for grpc endpoint configuration and registration.
/// </summary>
public static class GrpcEndpointExtensions
{
    /// <summary>
    /// Adds grpc server support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the add grpc server operation.</returns>
    public static LinkBuilder AddGrpcServer(this LinkBuilder builder, string name = "grpc")
        => AddGrpcServer(builder, ProtocolKey.Create(name));

    /// <summary>
    /// Adds grpc server support to the configuration.
    /// </summary>
    /// <param name="builder">The link builder to configure.</param>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the add grpc server operation.</returns>
    public static LinkBuilder AddGrpcServer(this LinkBuilder builder, ProtocolKey protocol)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddTransport(protocol, new GrpcServerProtocol());
    }

    /// <summary>
    /// Adds anyprotocol grpc support to the configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The result of the add anyprotocol grpc operation.</returns>
    public static IServiceCollection AddAnyProtocolGrpc(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGrpc();
        services.AddSingleton<GrpcEndpointMarker>();
        services.AddTransient<AnyProtocolGrpcService>(serviceProvider =>
            new AnyProtocolGrpcService(
                serviceProvider.GetRequiredService<RuntimePlan>(),
                serviceProvider.GetRequiredService<TransportRegistry>(),
                serviceProvider.GetRequiredService<MessageDispatcher>(),
                serviceProvider.GetService<IEnvelopeCodec>() ?? new BinaryEnvelopeCodec(),
                serviceProvider.GetRequiredService<IMessageEnvelopeFactory>(),
                serviceProvider.GetRequiredService<IRequestAdmission>()));
        return services;
    }

    /// <summary>
    /// Maps anyprotocol grpc endpoints into the application pipeline.
    /// </summary>
    /// <param name="endpoints">The endpoints.</param>
    /// <returns>The result of the map anyprotocol grpc operation.</returns>
    public static GrpcServiceEndpointConventionBuilder MapAnyProtocolGrpc(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        _ = endpoints.ServiceProvider.GetRequiredService<GrpcEndpointMarker>();
        foreach (var protocol in endpoints.ServiceProvider
                     .GetRequiredService<TransportRegistry>()
                     .Entries
                     .Select(static pair => pair.Value)
                     .OfType<GrpcServerProtocol>())
        {
            protocol.MarkMapped();
        }

        return endpoints.MapGrpcService<AnyProtocolGrpcService>();
    }

    private sealed class GrpcEndpointMarker;
}
