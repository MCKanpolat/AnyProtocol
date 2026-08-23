using AnyProtocol;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Mcp.AspNetCore;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Grpc.AspNetCore;
using AnyProtocol.Protocol.Rest.AspNetCore;
using AnyProtocol.Serializer.TextJson;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using OrderSystem.Contracts;
using OrderSystem.Server.Repositories;
using OrderSystem.Server.Services;

namespace OrderSystem.Server.Hosting;

public static class OrderSystemServer
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddRestServer(ProtocolKey.Rest)
            .AddGrpcServer(ProtocolKey.Grpc)
            .AddServer<IOrderService, OrderService>(server => server.UseProtocols(
                ProtocolKey.Rest,
                ProtocolKey.Grpc,
                ProtocolKey.Mcp)));
        services.AddAnyProtocolRest();
        services.AddAnyProtocolGrpc();
        services.AddAnyProtocolMcp();
        services.AddHealthChecks().AddAnyProtocolHealthChecks();
    }

    public static void MapEndpoints(WebApplication app)
    {
        var http1Endpoints = app.MapGroup(string.Empty).RequireHost("*:5080");

        http1Endpoints.MapGet("/", () => Results.Ok(new
        {
            Rest = "/api",
            Grpc = "AnyProtocol.Transport/Unary",
            Mcp = "/mcp",
            Liveness = "/health/live",
            Readiness = "/health/ready"
        }));
        http1Endpoints.MapAnyProtocol("/api");
        http1Endpoints.MapAnyProtocolMcpAllowAnonymousForDevelopment("/mcp");
        http1Endpoints.MapHealthChecks(
            "/health/live",
            new HealthCheckOptions
            {
                Predicate = registration =>
                    registration.Tags.Contains(AnyProtocolHealthChecks.LiveTag)
            });
        http1Endpoints.MapHealthChecks(
            "/health/ready",
            new HealthCheckOptions
            {
                Predicate = registration =>
                    registration.Tags.Contains(AnyProtocolHealthChecks.ReadyTag)
            });

        app.MapAnyProtocolGrpc().RequireHost("*:5081");
    }
}
