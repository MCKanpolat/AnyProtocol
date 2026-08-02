using AnyProtocol.Abstraction;
using System.Runtime.CompilerServices;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Validation;
using AnyProtocol.Validation.Abstraction;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Tests;

public sealed record PlaceOrderRequest(string OrderId);

public sealed record PlaceOrderResponse(string OrderId, int HandlerInstance);

public sealed record OrderPlaced(string OrderId);

public sealed record OrderStreamRequest(string Prefix, int Count);

public interface IOrderService
{
    [Channel("orders")]
    ValueTask<PlaceOrderResponse> PlaceAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken);

    [Channel("orders")]
    ValueTask<PlaceOrderResponse> FailAsync(PlaceOrderRequest request);
}

public interface IOrderStreamService
{
    IAsyncEnumerable<PlaceOrderResponse> WatchAsync(
        OrderStreamRequest request,
        CancellationToken cancellationToken);
}

public sealed class OrderService : IOrderService
{
    private static int _instances;
    private readonly int _instance = Interlocked.Increment(ref _instances);

    public ValueTask<PlaceOrderResponse> PlaceAsync(
        PlaceOrderRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new PlaceOrderResponse(request.OrderId, _instance));

    public ValueTask<PlaceOrderResponse> FailAsync(PlaceOrderRequest request)
        => throw new InvalidOperationException($"Order '{request.OrderId}' failed.");
}

public sealed class OrderStreamService : IOrderStreamService
{
    public async IAsyncEnumerable<PlaceOrderResponse> WatchAsync(
        OrderStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < request.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Prefix == "fail" && index == 2)
            {
                throw new InvalidOperationException("Emulated stream failed.");
            }

            yield return new PlaceOrderResponse($"{request.Prefix}-{index}", index);
            await Task.Yield();
        }
    }
}

public sealed class OrderPlacedHandler : IEventConsumer<OrderPlaced>
{
    public static TaskCompletionSource<OrderPlaced> Received { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask ConsumeAsync(OrderPlaced e)
    {
        Received.TrySetResult(e);
        return ValueTask.CompletedTask;
    }

}

public sealed class OrderValidator : IRequestValidator<PlaceOrderRequest>
{
    public ValueTask<IValidationResult> RequestAsync(PlaceOrderRequest request)
    {
        var result = new ValidationResult();
        if (request.OrderId == "invalid")
        {
            result.AddError(new ValidationError("Order id is invalid.", nameof(request.OrderId)));
        }

        return ValueTask.FromResult<IValidationResult>(result);
    }
}

public sealed class EndToEndTests
{
    [Fact]
    public async Task Proxy_dispatcher_and_scoped_handler_round_trip_over_in_memory()
    {
        var transport = new InMemoryMessagingProtocol();
        OrderPlacedHandler.Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddScoped<IRequestValidator<PlaceOrderRequest>, OrderValidator>();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", transport)
            .AddClient<IOrderService>(client => client.UseTransport("memory"))
            .AddServer<IOrderService, OrderService>(server => server.UseTransport("memory"))
            .AddEventHandler<OrderPlaced, OrderPlacedHandler>(
                handler => handler.UseTransport("memory"))
            .AddServerFilter(new ValidationFilter()));
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IAnyProtocolBus>();
        await bus.StartAsync();
        var client = provider.GetRequiredService<IOrderService>();

        var first = await client.PlaceAsync(new PlaceOrderRequest("one"), CancellationToken.None);
        var second = await client.PlaceAsync(new PlaceOrderRequest("two"), CancellationToken.None);

        Assert.Equal("one", first.OrderId);
        Assert.Equal("two", second.OrderId);
        Assert.NotEqual(first.HandlerInstance, second.HandlerInstance);

        await provider.GetRequiredService<IEventPublisher<OrderPlaced>>()
            .PublishAsync(new OrderPlaced("three"));
        var placed = await OrderPlacedHandler.Received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("three", placed.OrderId);

        var validation = await Assert.ThrowsAsync<AnyProtocolValidationException>(
            async () => await client.PlaceAsync(
                new PlaceOrderRequest("invalid"),
                CancellationToken.None));
        Assert.Single(validation.Fault.Details!);
    }

    [Fact]
    public async Task Handler_exception_round_trips_as_typed_fault()
    {
        var transport = new InMemoryMessagingProtocol();
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport("memory", transport)
            .AddClient<IOrderService>(client => client.UseTransport("memory"))
            .AddServer<IOrderService, OrderService>(server => server.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();
        var client = provider.GetRequiredService<IOrderService>();

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () => await client.FailAsync(new PlaceOrderRequest("broken")));

        Assert.Equal("handler_failed", exception.Fault.Code);
        Assert.Contains("broken", exception.Message);
    }

    [Fact]
    public async Task In_memory_stream_emulation_preserves_order_and_faults()
    {
        var transport = new InMemoryMessagingProtocol();
        var serializer = new TextJsonMessageSerializer();
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(serializer)
            .AddTransport("memory", transport)
            .AddClient<IOrderStreamService>(
                client => client
                    .UseTransport("memory")
                    .WithTimeout(TimeSpan.FromSeconds(5)))
            .AddServer<IOrderStreamService, OrderStreamService>(
                server => server.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();
        var client = provider.GetRequiredService<IOrderStreamService>();
        var values = new List<string>();
        var method = new ContractDescriptorFactory()
            .Create<IOrderStreamService>()
            .Methods
            .Single();
        await transport.SendAsync(
            method.Channel,
            new TransportEnvelope(
                new MessageHeaders
                {
                    [HeaderNames.Contract] = method.ContractName,
                    [HeaderNames.Method] = method.MethodName,
                    [HeaderNames.MessageType] = MessageType.Request.ToString()
                },
                serializer.Serialize(new OrderStreamRequest("ignored", 1))));

        await foreach (var item in client.WatchAsync(
                           new OrderStreamRequest("order", 5),
                           CancellationToken.None))
        {
            values.Add(item.OrderId);
        }

        var partial = new List<string>();
        var fault = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            async () =>
            {
                await foreach (var item in client.WatchAsync(
                                   new OrderStreamRequest("fail", 5),
                                   CancellationToken.None))
                {
                    partial.Add(item.OrderId);
                }
            });

        Assert.Equal(
            Enumerable.Range(0, 5).Select(index => $"order-{index}"),
            values);
        Assert.Equal(["fail-0", "fail-1"], partial);
        Assert.Equal("handler_failed", fault.Fault.Code);
    }
}
