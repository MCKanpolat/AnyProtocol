using AnyProtocol;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Mcp.AspNetCore;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Grpc.AspNetCore;
using AnyProtocol.Protocol.Rest.AspNetCore;
using AnyProtocol.Serializer.TextJson;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

if (args.Contains("--stdio", StringComparer.Ordinal))
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    ConfigureAnyProtocol(
        builder.Services,
        [ProtocolKey.Mcp],
        _ => { });
    builder.Services.AddAnyProtocolMcpStdio();
    await builder.Build().RunAsync();
    return;
}

var webBuilder = WebApplication.CreateBuilder(args);
ConfigureAnyProtocol(
    webBuilder.Services,
    [ProtocolKey.Rest, ProtocolKey.Grpc, ProtocolKey.Mcp],
    link => link
        .AddRestServer(ProtocolKey.Rest)
        .AddGrpcServer(ProtocolKey.Grpc));
webBuilder.Services.AddAnyProtocolRest();
webBuilder.Services.AddAnyProtocolGrpc();
webBuilder.Services.AddAnyProtocolMcp();
webBuilder.Services.AddHealthChecks().AddAnyProtocolHealthChecks();

var app = webBuilder.Build();
app.MapGet("/", () => Results.Ok(new
{
    Rest = "/api",
    Grpc = "AnyProtocol.Transport/Unary",
    Mcp = "/mcp",
    Liveness = "/health/live",
    Readiness = "/health/ready"
}));
app.MapAnyProtocol("/api");
app.MapAnyProtocolGrpc();
app.MapAnyProtocolMcpAllowAnonymousForDevelopment("/mcp");
app.MapHealthChecks(
    "/health/live",
    new HealthCheckOptions
    {
        Predicate = registration =>
            registration.Tags.Contains(AnyProtocolHealthChecks.LiveTag)
    });
app.MapHealthChecks(
    "/health/ready",
    new HealthCheckOptions
    {
        Predicate = registration =>
            registration.Tags.Contains(AnyProtocolHealthChecks.ReadyTag)
    });
await app.RunAsync();

static void ConfigureAnyProtocol(
    IServiceCollection services,
    IReadOnlyList<ProtocolKey> protocols,
    Action<AnyProtocol.Configuration.LinkBuilder> addTransport)
{
    services.AddAnyProtocol(
        link =>
        {
            link.UseSerializer(new TextJsonMessageSerializer());
            addTransport(link);
            link.AddServer<IOrderService, OrderService>(
                server => server.UseProtocols(protocols.ToArray()));
        });
}

public sealed record GetOrderRequest(string OrderId);

public sealed record OrderResponse(string OrderId, string Status);

public interface IOrderService
{
    [McpTool(
        Name = "orders_get",
        Description = "Gets an order by its identifier.",
        ReadOnly = true,
        Idempotent = true)]
    ValueTask<OrderResponse> GetAsync(
        GetOrderRequest request,
        CancellationToken cancellationToken);
}

public sealed class OrderService : IOrderService
{
    public ValueTask<OrderResponse> GetAsync(
        GetOrderRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new OrderResponse(request.OrderId, "ready"));
}
