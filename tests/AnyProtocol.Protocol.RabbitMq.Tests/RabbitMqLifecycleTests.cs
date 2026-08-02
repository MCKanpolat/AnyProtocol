using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using System.Diagnostics;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqLifecycleTests(RabbitMqFixture fixture)
    : IClassFixture<RabbitMqFixture>
{
    [Fact]
    public void Semantics_are_durable_at_least_once_and_per_channel()
    {
        var transport = new RabbitMqMessagingProtocol(new RabbitMqProtocolOptions
        {
            ConnectionUri = new Uri("amqp://guest:guest@localhost:5672/")
        });

        Assert.Equal(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers |
            TransportCapabilities.NativeHeaders,
            transport.Capabilities);
        Assert.Equal(TransportDeliveryGuarantee.AtLeastOnce, transport.Semantics.DeliveryGuarantee);
        Assert.Equal(TransportOrdering.PerChannel, transport.Semantics.Ordering);
        Assert.Equal(TransportDurability.Durable, transport.Semantics.Durability);
        Assert.True(transport.Semantics.SupportsBackpressure);
        Assert.False(transport.Semantics.SupportsNativeRequestReply);
        Assert.False(transport.Semantics.SupportsNativeStreaming);
    }

    [SkippableFact]
    public async Task Readiness_is_ready_with_broker_and_not_ready_after_disposal()
    {
        fixture.RequireRabbitMq();
        var transport = fixture.CreateTransport();
        var readiness = Assert.IsAssignableFrom<ITransportReadiness>(transport);

        var ready = await readiness.CheckReadinessAsync();
        await transport.DisposeAsync();
        var disposed = await readiness.CheckReadinessAsync();

        Assert.Equal(TransportReadinessState.Ready, ready.State);
        Assert.Equal(TransportReadinessState.NotReady, disposed.State);
        Assert.DoesNotContain("guest", disposed.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Disposal_rejects_new_sends()
    {
        fixture.RequireRabbitMq();
        var transport = fixture.CreateTransport();
        await transport.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await transport.SendAsync(
                "orders.disposed",
                new TransportEnvelope(
                    new MessageHeaders(),
                    ReadOnlyMemory<byte>.Empty)));
    }

    [SkippableFact]
    public async Task Disposal_cancels_an_active_handler_within_shutdown_timeout()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions() with
        {
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        };
        var transport = new RabbitMqMessagingProtocol(options);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = await transport.SubscribeAsync(
            "orders.shutdown",
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });
        await transport.SendAsync(
            "orders.shutdown",
            new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var stopwatch = Stopwatch.StartNew();
        await transport.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        await subscription.DisposeAsync();
    }
}
