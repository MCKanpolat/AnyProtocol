using AnyProtocol;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Mcp;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using OrderSystem.Contracts;
using OrderSystem.Server.Hosting;
using GrpcClientProtocol = AnyProtocol.Protocol.Grpc.GrpcMessagingProtocol;
using RestClientProtocol = AnyProtocol.Protocol.Rest.RestMessagingProtocol;

namespace OrderSystem.MultiProtocol.Tests;

public sealed class MultiProtocolIntegrationTests
{
    [Fact]
    public async Task Rest_and_grpc_execute_the_same_order_scenario()
    {
        await using var host = await CreateHostAsync();
        await using var rest = CreateRestClient(CreateHttpClient(host, 5080));
        await using var grpc = CreateGrpcClient(host);

        var restOrder = await rest.GetRequiredService<IOrderService>().CreateAsync(
            new CreateOrderRequest("rest-customer", 42m), CancellationToken.None);
        var grpcOrder = await grpc.GetRequiredService<IOrderService>().CreateAsync(
            new CreateOrderRequest("grpc-customer", 84m), CancellationToken.None);

        Assert.Equal(restOrder, await rest.GetRequiredService<IOrderService>().GetAsync(
            new GetOrderRequest(restOrder.OrderId), CancellationToken.None));
        Assert.Equal(grpcOrder, await grpc.GetRequiredService<IOrderService>().GetAsync(
            new GetOrderRequest(grpcOrder.OrderId), CancellationToken.None));

        var restList = await rest.GetRequiredService<IOrderService>().ListAsync(
            new ListOrdersRequest(), CancellationToken.None);
        var grpcList = await grpc.GetRequiredService<IOrderService>().ListAsync(
            new ListOrdersRequest(), CancellationToken.None);

        Assert.Contains(restOrder, restList.Orders);
        Assert.Contains(grpcOrder, restList.Orders);
        Assert.Contains(restOrder, grpcList.Orders);
        Assert.Contains(grpcOrder, grpcList.Orders);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("grpc")]
    public async Task Missing_order_fault_survives_transport(string protocol)
    {
        await using var host = await CreateHostAsync();
        await using var client = protocol == "rest"
            ? CreateRestClient(CreateHttpClient(host, 5080))
            : CreateGrpcClient(host);

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await client.GetRequiredService<IOrderService>().GetAsync(
                new GetOrderRequest("ORD-404"), CancellationToken.None));

        Assert.Equal("order_not_found", exception.Fault.Code);
    }

    [Fact]
    public async Task Server_exposes_three_mcp_tools_and_health_endpoints()
    {
        await using var host = await CreateHostAsync();
        var tools = host.Services.GetRequiredService<McpToolCatalog>().Tools;

        Assert.Equal(
            ["orders_create", "orders_get", "orders_list"],
            tools.Select(static tool => tool.Name).Order());
        using var httpClient = CreateHttpClient(host, 5080);
        Assert.Equal("Healthy", await httpClient.GetStringAsync("/health/live"));
        Assert.Equal("Healthy", await httpClient.GetStringAsync("/health/ready"));
    }

    [Fact]
    public async Task Discovery_advertises_all_protocol_and_health_routes()
    {
        await using var host = await CreateHostAsync();
        using var httpClient = CreateHttpClient(host, 5080);

        using var discovery = System.Text.Json.JsonDocument.Parse(
            await httpClient.GetStringAsync("/"));
        var root = discovery.RootElement;

        Assert.Equal("/api", root.GetProperty("rest").GetString());
        Assert.Equal("AnyProtocol.Transport/Unary", root.GetProperty("grpc").GetString());
        Assert.Equal("/mcp", root.GetProperty("mcp").GetString());
        Assert.Equal("/health/live", root.GetProperty("liveness").GetString());
        Assert.Equal("/health/ready", root.GetProperty("readiness").GetString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Http1_endpoints_are_available_only_on_port_5080(string path)
    {
        await using var host = await CreateHostAsync();
        using var http1Client = CreateHttpClient(host, 5080);
        using var http2Client = CreateHttpClient(host, 5081);

        using var expectedListenerResponse = await http1Client.GetAsync(path);
        using var wrongListenerResponse = await http2Client.GetAsync(path);

        Assert.True(expectedListenerResponse.IsSuccessStatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrongListenerResponse.StatusCode);
    }

    [Fact]
    public async Task Rest_is_available_only_on_port_5080()
    {
        await using var host = await CreateHostAsync();
        await using var rest = CreateRestClient(CreateHttpClient(host, 5080));
        await using var wrongPortRest = CreateRestClient(CreateHttpClient(host, 5081));

        var created = await rest.GetRequiredService<IOrderService>().CreateAsync(
            new CreateOrderRequest("rest-listener", 42m), CancellationToken.None);

        Assert.Equal("rest-listener", created.CustomerId);
        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await wrongPortRest.GetRequiredService<IOrderService>().CreateAsync(
                new CreateOrderRequest("wrong-rest-listener", 42m), CancellationToken.None));

        Assert.Equal("http_error", exception.Fault.Code);
    }

    [Fact]
    public async Task Grpc_is_available_only_on_port_5081()
    {
        await using var host = await CreateHostAsync();
        await using var grpc = CreateGrpcClient(host, 5081);
        await using var wrongPortGrpc = CreateGrpcClient(host, 5080);

        var created = await grpc.GetRequiredService<IOrderService>().CreateAsync(
            new CreateOrderRequest("grpc-listener", 84m), CancellationToken.None);

        Assert.Equal("grpc-listener", created.CustomerId);
        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await wrongPortGrpc.GetRequiredService<IOrderService>().CreateAsync(
                new CreateOrderRequest("wrong-grpc-listener", 84m), CancellationToken.None));

        var grpcException = Assert.IsType<Grpc.Core.RpcException>(exception.InnerException);
        Assert.Equal(Grpc.Core.StatusCode.Unimplemented, grpcException.StatusCode);
    }

    [Fact]
    public async Task Mcp_is_available_only_on_port_5080()
    {
        await using var host = await CreateHostAsync();
        using var http1Client = CreateHttpClient(host, 5080);
        using var http2Client = CreateHttpClient(host, 5081);
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http1Client.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "OrderSystem listener test"
            },
            http1Client);
        await using var mcpClient = await McpClient.CreateAsync(transport);

        var tools = await mcpClient.ListToolsAsync();

        Assert.Equal(
            ["orders_create", "orders_get", "orders_list"],
            tools.Select(static tool => tool.Name).Order());

        await using var wrongPortTransport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http2Client.BaseAddress!, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                Name = "OrderSystem wrong-listener test"
            },
            http2Client);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await McpClient.CreateAsync(wrongPortTransport));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, exception.StatusCode);
    }

    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        OrderSystemServer.ConfigureServices(builder.Services);

        var app = builder.Build();
        OrderSystemServer.MapEndpoints(app);
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider CreateRestClient(HttpClient httpClient)
    {
        var serializer = new TextJsonMessageSerializer();
        var transport = new RestClientProtocol(httpClient, serializer, "/api");
        return CreateClient(ProtocolKey.Rest, transport, serializer);
    }

    private static ServiceProvider CreateGrpcClient(WebApplication host, int port = 5081)
    {
        var channel = GrpcChannel.ForAddress(
            $"http://localhost:{port}",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var transport = new GrpcClientProtocol(channel, disposeChannel: true);
        return CreateClient(ProtocolKey.Grpc, transport, new TextJsonMessageSerializer());
    }

    private static ServiceProvider CreateClient(
        ProtocolKey protocol,
        IMessagingProtocol transport,
        TextJsonMessageSerializer serializer)
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(serializer)
            .AddTransport(protocol, transport)
            .AddClient<IOrderService>(client => client.UseProtocol(protocol)));
        return services.BuildServiceProvider();
    }

    private static HttpClient CreateHttpClient(WebApplication host, int port)
    {
        var client = host.GetTestClient();
        client.BaseAddress = new Uri($"http://localhost:{port}");
        return client;
    }
}
