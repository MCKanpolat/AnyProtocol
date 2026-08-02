using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.Rest.AspNetCore;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Validation;
using AnyProtocol.Validation.Abstraction;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RestClientProtocol = AnyProtocol.Protocol.Rest.RestMessagingProtocol;

namespace AnyProtocol.Protocol.Rest.Tests;

public sealed record RestOrderRequest(string OrderId);

public sealed record RestOrderResponse(string OrderId);

public sealed record RestOrderPlaced(string OrderId);

public interface IRestOrderService
{
    [Channel("rest/orders")]
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

public sealed class RestOrderValidator : IRequestValidator<RestOrderRequest>
{
    public ValueTask<IValidationResult> RequestAsync(RestOrderRequest request)
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

    private static async Task<WebApplication> CreateHostAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped<IRequestValidator<RestOrderRequest>, RestOrderValidator>();
        builder.Services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddRestServer()
            .AddServer<IRestOrderService, RestOrderService>(
                server => server.UseTransport("rest"))
            .AddEventHandler<RestOrderPlaced, RestOrderPlacedHandler>(
                handler => handler
                    .UseTransport("rest")
                    .UseChannel("rest/events"))
            .AddServerFilter(new ValidationFilter()));
        builder.Services.AddAnyProtocolRest();

        var app = builder.Build();
        app.MapGet("/health", () => "ok");
        app.MapAnyProtocol();
        await app.StartAsync();
        return app;
    }

    private static ServiceProvider CreateClientProvider(
        HttpClient httpClient,
        TimeSpan? timeout = null)
    {
        var serializer = new TextJsonMessageSerializer();
        var transport = new RestClientProtocol(httpClient, serializer);
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(serializer)
            .AddTransport("rest", transport)
            .AddClient<IRestOrderService>(
                client => client
                    .UseTransport("rest")
                    .WithTimeout(timeout ?? TimeSpan.FromSeconds(5))));
        return services.BuildServiceProvider();
    }
}
