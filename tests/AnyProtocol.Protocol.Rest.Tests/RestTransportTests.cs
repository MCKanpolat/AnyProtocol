using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Rest;
using AnyProtocol.Protocol.Rest.AspNetCore;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Validation;
using AnyProtocol.Validation.Abstraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RestClientProtocol = AnyProtocol.Protocol.Rest.RestMessagingProtocol;

namespace AnyProtocol.Protocol.Rest.Tests;

public sealed record RestOrderRequest(string OrderId);

public sealed record RestOrderResponse(string OrderId);

public sealed record RestOrderPlaced(string OrderId);

[RestRoute("rest-api/orders")]
public interface IRestOrderService
{
    [Channel("rest/orders")]
    [RestRoute("place-order")]
    [RestHttpMethod(RestHttpMethods.Post)]
    ValueTask<RestOrderResponse> PlaceAsync(
        RestOrderRequest request,
        CancellationToken cancellationToken);

    [Channel("rest/orders")]
    ValueTask<RestOrderResponse> FailAsync(RestOrderRequest request);

    [Channel("rest/orders")]
    ValueTask NotifyAsync(RestOrderRequest request);
}

public sealed class RestOrderService : IRestOrderService
{
    public static TaskCompletionSource<string> Notification { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask<RestOrderResponse> PlaceAsync(
        RestOrderRequest request,
        CancellationToken cancellationToken)
    {
        if (request.OrderId == "slow")
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return new RestOrderResponse(request.OrderId);
    }

    public ValueTask<RestOrderResponse> FailAsync(RestOrderRequest request)
        => throw new InvalidOperationException($"Order '{request.OrderId}' failed.");

    public ValueTask NotifyAsync(RestOrderRequest request)
    {
        Notification.TrySetResult(request.OrderId);
        return ValueTask.CompletedTask;
    }
}

public interface IAttributeRestOrderService
{
    [Channel("rest/attribute-orders")]
    [HttpPut("ship-order")]
    ValueTask<RestOrderResponse> ShipAsync(RestOrderRequest request);
}

[Route("attribute-orders")]
public sealed class AttributeRestOrderService : IAttributeRestOrderService
{
    [HttpPut("ship-order")]
    public ValueTask<RestOrderResponse> ShipAsync(RestOrderRequest request)
        => ValueTask.FromResult(new RestOrderResponse(request.OrderId));
}

public interface IFallbackRestOrderService
{
    [Channel("rest/fallback-orders")]
    ValueTask<RestOrderResponse> ExecuteAsync(RestOrderRequest request);
}

public sealed class FallbackRestOrderService : IFallbackRestOrderService
{
    public ValueTask<RestOrderResponse> ExecuteAsync(RestOrderRequest request)
        => ValueTask.FromResult(new RestOrderResponse(request.OrderId));
}

[RestRoute("shared/orders")]
public interface ISharedRouteRestOrderService
{
    [Channel("rest/shared-orders")]
    [RestRoute("order")]
    [RestHttpMethod(RestHttpMethods.Get)]
    ValueTask<RestOrderResponse> ReadAsync(RestOrderRequest request);

    [Channel("rest/shared-orders")]
    [RestRoute("order")]
    [RestHttpMethod(RestHttpMethods.Post)]
    ValueTask<RestOrderResponse> WriteAsync(RestOrderRequest request);
}

public sealed class SharedRouteRestOrderService : ISharedRouteRestOrderService
{
    public ValueTask<RestOrderResponse> ReadAsync(RestOrderRequest request)
        => ValueTask.FromResult(new RestOrderResponse($"read:{request.OrderId}"));

    public ValueTask<RestOrderResponse> WriteAsync(RestOrderRequest request)
        => ValueTask.FromResult(new RestOrderResponse($"write:{request.OrderId}"));
}

public sealed class RestOrderValidator : IRequestValidator<RestOrderRequest>
{
    public ValueTask<IValidationResult> ValidateAsync(
        RestOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = new ValidationResult();
        if (request.OrderId == "invalid")
        {
            result.AddError(new ValidationError("Invalid order id.", nameof(request.OrderId)));
        }

        return ValueTask.FromResult<IValidationResult>(result);
    }
}

public sealed class RestOrderPlacedHandler : IEventConsumer<RestOrderPlaced>
{
    public static TaskCompletionSource<RestOrderPlaced> Received { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask ConsumeAsync(RestOrderPlaced e)
    {
        Received.TrySetResult(e);
        return ValueTask.CompletedTask;
    }
}

public sealed class RestTransportTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("G ET")]
    [InlineData("GET/POST")]
    public void Rest_http_method_attribute_rejects_invalid_tokens(string method)
    {
        Assert.Throws<ArgumentException>(() => new RestHttpMethodAttribute(method));
    }

    [Fact]
    public void Rest_http_method_attribute_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => new RestHttpMethodAttribute(null!));
    }

    [Fact]
    public void Rest_http_method_attribute_normalizes_custom_methods()
    {
        var attribute = new RestHttpMethodAttribute(" propfind ");

        Assert.Equal("PROPFIND", attribute.Method);
    }

    [Fact]
    public void Public_api_retains_legacy_request_and_constructor_signatures()
    {
        Assert.NotNull(typeof(RestClientProtocol).GetConstructor(
            [typeof(HttpClient), typeof(IMessageSerializer), typeof(string)]));
        Assert.NotNull(typeof(RestClientProtocol).GetConstructor(
            [
                typeof(IHttpClientFactory),
                typeof(IMessageSerializer),
                typeof(string),
                typeof(string)
            ]));
        Assert.NotNull(typeof(RequestReplyEngine).GetMethod(
            nameof(RequestReplyEngine.RequestAsync),
            [
                typeof(string),
                typeof(TransportEnvelope),
                typeof(TimeSpan),
                typeof(CancellationToken)
            ]));
    }

    [Fact]
    public void Configuration_rejects_two_rest_server_protocols_for_one_contract()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new Configuration.LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddRestServer(ProtocolKey.Create("internal-rest"))
                .AddRestServer(ProtocolKey.Create("external-rest"))
                .AddServer<IRestOrderService, RestOrderService>(server => server.UseProtocols(
                    ProtocolKey.Create("internal-rest"),
                    ProtocolKey.Create("external-rest")))
                .Build());

        Assert.Contains("native server", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("internal-rest", exception.Message);
        Assert.Contains("external-rest", exception.Message);
    }

    [Fact]
    public async Task Server_readiness_requires_endpoint_mapping()
    {
        var protocol = new RestServerProtocol();

        var readiness = await protocol.CheckReadinessAsync();

        Assert.Equal(TransportReadinessState.NotReady, readiness.State);
    }

    [Fact]
    public async Task Minimal_api_round_trips_proxy_and_coexists_with_regular_endpoints()
    {
        RestOrderService.Notification =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host.GetTestClient());
        var client = clientProvider.GetRequiredService<IRestOrderService>();

        var response = await client.PlaceAsync(
            new RestOrderRequest("order-1"),
            CancellationToken.None);
        var health = await host.GetTestClient().GetStringAsync("/health");
        await client.NotifyAsync(new RestOrderRequest("notice-1"));
        var notification = await RestOrderService.Notification.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("order-1", response.OrderId);
        Assert.Equal("ok", health);
        Assert.Equal("notice-1", notification);
        var serverReadiness = await ((ITransportReadiness)host.Services
                .GetRequiredService<TransportRegistry>()
                .GetRequired("rest"))
            .CheckReadinessAsync();
        Assert.Equal(TransportReadinessState.Ready, serverReadiness.State);
    }

    [Fact]
    public async Task Operation_endpoints_dispatch_typed_calls_and_expose_openapi_metadata()
    {
        await using var host = await CreateHostAsync(
            options =>
            {
                options.MapOperationEndpoints = true;
                options.FallbackOperationRouteResolver = method =>
                    method.ContractType == typeof(IFallbackRestOrderService)
                        ? "fallback/execute-order"
                        : null;
                options.FallbackOperationHttpMethodResolver = method =>
                    method.ContractType == typeof(IFallbackRestOrderService)
                        ? "PATCH"
                        : "POST";
            });

        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(
                new RestOrderRequest("operation-route"),
                new System.Text.Json.JsonSerializerOptions()),
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await host.GetTestClient().PostAsync(
            "/anyprotocol/rest-api/orders/place-order",
            content);
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Unexpected status {(int)response.StatusCode}: {responseBody}");
        response.EnsureSuccessStatusCode();
        Assert.Contains("operation-route", responseBody);
        var result = System.Text.Json.JsonSerializer.Deserialize<RestOrderResponse>(
            responseBody,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(result);
        Assert.Equal("operation-route", result.OrderId);

        var endpoint = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/anyprotocol/rest-api/orders/place-order");
        var accepts = endpoint.Metadata.GetMetadata<IAcceptsMetadata>();
        var produces = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();

        Assert.NotNull(accepts);
        Assert.Equal(typeof(RestOrderRequest), accepts.RequestType);
        Assert.Contains(
            produces,
            metadata => metadata.StatusCode == 200 &&
                        metadata.Type == typeof(RestOrderResponse) &&
                        metadata.ContentTypes.Contains("application/json"));
        Assert.Contains(
            produces,
            metadata => metadata.StatusCode == 400 &&
                        metadata.ContentTypes.Contains("application/problem+json"));
        Assert.Equal(
            "AnyProtocol_AnyProtocol_Protocol_Rest_Tests_IRestOrderService_PlaceAsync",
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);

        await using var operationClientProvider = CreateClientProvider(
            host.GetTestClient(),
            httpMethodResolver: method =>
                method.ContractType == typeof(IFallbackRestOrderService)
                    ? "PATCH"
                    : "POST");
        var attributeClient = operationClientProvider
            .GetRequiredService<IAttributeRestOrderService>();
        var attributeResult = await attributeClient.ShipAsync(
            new RestOrderRequest("attribute-route"));
        Assert.Equal("attribute-route", attributeResult.OrderId);

        var attributeEndpoint = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/anyprotocol/attribute-orders/ship-order");
        Assert.Contains(
            "PUT",
            attributeEndpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? []);

        var fallbackClient = operationClientProvider
            .GetRequiredService<IFallbackRestOrderService>();
        var fallbackResult = await fallbackClient.ExecuteAsync(
            new RestOrderRequest("fallback-route"));
        Assert.Equal("fallback-route", fallbackResult.OrderId);
    }

    [Fact]
    public async Task Operation_endpoints_allow_one_route_with_different_http_methods()
    {
        await using var host = await CreateHostAsync(
            options => options.MapOperationEndpoints = true);
        var endpoints = host.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(candidate =>
                candidate.RoutePattern.RawText == "/anyprotocol/shared/orders/order")
            .ToArray();

        Assert.Equal(2, endpoints.Length);
        Assert.Contains(
            endpoints,
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods.Contains("GET") == true);
        Assert.Contains(
            endpoints,
            endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods.Contains("POST") == true);

        async Task<RestOrderResponse?> SendAsync(string method, string orderId)
        {
            using var request = new HttpRequestMessage(
                new HttpMethod(method),
                "/anyprotocol/shared/orders/order")
            {
                Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(new RestOrderRequest(orderId)),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
            using var response = await host.GetTestClient().SendAsync(request);
            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync();
            return System.Text.Json.JsonSerializer.Deserialize<RestOrderResponse>(
                responseBody,
                new System.Text.Json.JsonSerializerOptions(
                    System.Text.Json.JsonSerializerDefaults.Web));
        }

        Assert.Equal("read:one", (await SendAsync("GET", "one"))?.OrderId);
        Assert.Equal("write:two", (await SendAsync("POST", "two"))?.OrderId);
    }

    [Fact]
    public async Task Minimal_api_dispatches_rest_events()
    {
        RestOrderPlacedHandler.Received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await CreateHostAsync();
        var serializer = new TextJsonMessageSerializer();
        var transport = new RestClientProtocol(host.GetTestClient(), serializer);
        var message = new RestOrderPlaced("event-1");
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.Channel] = "rest/events",
            [HeaderNames.Contract] = typeof(RestOrderPlaced).FullName,
            [HeaderNames.MessageType] = MessageType.Event.ToString(),
            [HeaderNames.ContentType] = "application/x-anyprotocol"
        };

        await transport.SendAsync(
            "rest/events",
            new TransportEnvelope(headers, serializer.Serialize(message)));
        var received = await RestOrderPlacedHandler.Received.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("event-1", received.OrderId);
    }

    [Fact]
    public async Task Problem_details_round_trips_as_anyprotocol_faults()
    {
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(host.GetTestClient());
        var client = clientProvider.GetRequiredService<IRestOrderService>();

        var validation = await Assert.ThrowsAsync<AnyProtocolValidationException>(
            async () => await client.PlaceAsync(
                new RestOrderRequest("invalid"),
                CancellationToken.None));
        var handlerFault = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await client.FailAsync(new RestOrderRequest("broken")));

        Assert.Equal("validation_failed", validation.Fault.Code);
        Assert.Single(validation.Fault.Details!);
        Assert.Equal("handler_failed", handlerFault.Fault.Code);
    }

    [Fact]
    public async Task Client_timeout_cancels_http_request()
    {
        await using var host = await CreateHostAsync();
        await using var clientProvider = CreateClientProvider(
            host.GetTestClient(),
            TimeSpan.FromMilliseconds(50));
        var client = clientProvider.GetRequiredService<IRestOrderService>();

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await client.PlaceAsync(
                new RestOrderRequest("slow"),
                CancellationToken.None));
    }

    private static async Task<WebApplication> CreateHostAsync(
        Action<RestEndpointOptions>? configureRest = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<IRequestValidator<RestOrderRequest>, RestOrderValidator>();
        builder.Services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddRestServer()
            .AddServer<IRestOrderService, RestOrderService>(
                server => server.UseTransport("rest"))
            .AddServer<IAttributeRestOrderService, AttributeRestOrderService>(
                server => server.UseTransport("rest"))
            .AddServer<IFallbackRestOrderService, FallbackRestOrderService>(
                server => server.UseTransport("rest"))
            .AddServer<ISharedRouteRestOrderService, SharedRouteRestOrderService>(
                server => server.UseTransport("rest"))
            .AddEventHandler<RestOrderPlaced, RestOrderPlacedHandler>(
                handler => handler
                    .UseTransport("rest")
                    .UseChannel("rest/events"))
            .AddServerFilter(new ValidationFilter()));
        builder.Services.AddAnyProtocolRest(configureRest);

        var app = builder.Build();
        app.MapGet("/health", () => "ok");
        app.MapAnyProtocol();
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider CreateClientProvider(
        HttpClient httpClient,
        TimeSpan? timeout = null,
        Func<ContractMethodDescriptor, string?>? httpMethodResolver = null)
    {
        var serializer = new TextJsonMessageSerializer();
        var transport = new RestClientProtocol(
            httpClient,
            serializer,
            "/anyprotocol",
            httpMethodResolver);
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(serializer)
            .AddTransport("rest", transport)
            .AddClient<IRestOrderService>(
                client => client
                    .UseTransport("rest")
                    .WithTimeout(timeout ?? TimeSpan.FromSeconds(5)))
            .AddClient<IAttributeRestOrderService>(
                client => client
                    .UseTransport("rest")
                    .WithTimeout(timeout ?? TimeSpan.FromSeconds(5)))
            .AddClient<IFallbackRestOrderService>(
                client => client
                    .UseTransport("rest")
                    .WithTimeout(timeout ?? TimeSpan.FromSeconds(5))));
        return services.BuildServiceProvider();
    }
}
