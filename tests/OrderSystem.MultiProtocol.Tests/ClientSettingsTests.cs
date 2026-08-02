using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using OrderSystem.Client.Configuration;
using OrderSystem.Client.Services;
using OrderSystem.Contracts;

namespace OrderSystem.MultiProtocol.Tests;

public sealed class ClientSettingsTests
{
    [Theory]
    [InlineData("Rest", 5080)]
    [InlineData("Grpc", 5081)]
    public void Selects_the_uri_configured_for_the_requested_protocol(string protocol, int expectedPort)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:ServerUri"] = "http://localhost:5999",
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081"
            })
            .Build();

        var settings = ClientSettings.From(configuration, ["--protocol", protocol]);

        Assert.Equal(expectedPort, settings.ServerUri.Port);
    }

    [Fact]
    public void Defaults_to_rest_when_protocol_is_not_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081"
            })
            .Build();

        var settings = ClientSettings.From(configuration, []);

        Assert.Equal(ProtocolKey.Rest, settings.Protocol);
    }

    [Fact]
    public void Defaults_to_rest_and_allows_grpc_override()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081",
                ["OrderSystem:Protocol"] = "Rest"
            })
            .Build();

        var settings = ClientSettings.From(configuration, ["--protocol", "Grpc", "--show-error"]);

        Assert.Equal(ProtocolKey.Grpc, settings.Protocol);
        Assert.Equal(new Uri("http://localhost:5081"), settings.ServerUri);
        Assert.True(settings.ShowError);
    }

    [Fact]
    public void Rejects_unknown_protocol_with_supported_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081",
                ["OrderSystem:Protocol"] = "Kafka"
            })
            .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => ClientSettings.From(configuration, []));

        Assert.Contains("Rest", exception.Message);
        Assert.Contains("Grpc", exception.Message);
    }

    [Fact]
    public void Accepts_case_insensitive_protocol_values()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081",
                ["OrderSystem:Protocol"] = "gRpC"
            })
            .Build();

        var settings = ClientSettings.From(configuration, []);

        Assert.Equal(ProtocolKey.Grpc, settings.Protocol);
    }

    [Fact]
    public void Rejects_protocol_option_without_a_value()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = "http://localhost:5080",
                ["OrderSystem:GrpcUri"] = "http://localhost:5081"
            })
            .Build();

        Assert.Throws<ArgumentException>(() => ClientSettings.From(configuration, ["--protocol"]));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("ftp://localhost/orders")]
    public void Rejects_non_http_absolute_rest_uris(string serverUri)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderSystem:RestUri"] = serverUri,
                ["OrderSystem:GrpcUri"] = "http://localhost:5081"
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ClientSettings.From(configuration, []));
    }
}

public sealed class OrderScenarioTests
{
    [Fact]
    public async Task Creates_gets_and_lists_before_printing_the_expected_missing_order_fault()
    {
        var service = new RecordingOrderService();
        using var output = new StringWriter();

        await OrderScenario.RunAsync(
            service,
            protocol: ProtocolKey.Grpc,
            showError: true,
            output: output,
            cancellationToken: CancellationToken.None);

        Assert.Equal(["create", "get:ORD-1001", "list", "get:ORD-404"], service.Calls);
        Assert.StartsWith($"Protocol: Grpc{Environment.NewLine}", output.ToString());
        Assert.Contains("ORD-1001", output.ToString());
        Assert.Contains("order_not_found", output.ToString());
        Assert.Contains("Order not found.", output.ToString());
    }

    [Fact]
    public async Task Skips_the_missing_order_lookup_when_show_error_is_false()
    {
        var service = new RecordingOrderService();
        using var output = new StringWriter();

        await OrderScenario.RunAsync(
            service,
            protocol: ProtocolKey.Rest,
            showError: false,
            output: output,
            cancellationToken: CancellationToken.None);

        Assert.Equal(["create", "get:ORD-1001", "list"], service.Calls);
        Assert.StartsWith($"Protocol: Rest{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Propagates_an_unexpected_anyprotocol_fault()
    {
        var service = new RecordingOrderService { MissingOrderFaultCode = "service_unavailable" };
        using var output = new StringWriter();

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await OrderScenario.RunAsync(
                service,
                protocol: ProtocolKey.Rest,
                showError: true,
                output: output,
                cancellationToken: CancellationToken.None));

        Assert.Equal("service_unavailable", exception.Fault.Code);
    }

    private sealed class RecordingOrderService : IOrderService
    {
        public List<string> Calls { get; } = [];

        public string MissingOrderFaultCode { get; init; } = "order_not_found";

        public ValueTask<OrderResponse> CreateAsync(
            CreateOrderRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add("create");
            return ValueTask.FromResult(new OrderResponse("ORD-1001", request.CustomerId, request.Total, "ready"));
        }

        public ValueTask<OrderResponse> GetAsync(
            GetOrderRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add($"get:{request.OrderId}");
            if (request.OrderId == "ORD-404")
            {
                throw new AnyProtocolFaultException(
                    new FaultMessage(MissingOrderFaultCode, "Order not found."));
            }

            return ValueTask.FromResult(new OrderResponse(request.OrderId, "customer-42", 125.50m, "ready"));
        }

        public ValueTask<OrderListResponse> ListAsync(
            ListOrdersRequest request,
            CancellationToken cancellationToken)
        {
            Calls.Add("list");
            return ValueTask.FromResult(
                new OrderListResponse([new OrderResponse("ORD-1001", "customer-42", 125.50m, "ready")]));
        }
    }
}

public sealed class ClientConnectionFailureTests
{
    [Fact]
    public void Treats_an_unavailable_grpc_call_as_a_connection_failure()
    {
        var exception = new RpcException(new Status(StatusCode.Unavailable, "Server is unavailable."));

        Assert.True(ConnectionFailure.IsConnectionLevel(exception));
    }
}
