using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqProtocolValidationTests
{
    [Fact]
    public async Task Protocol_validates_inputs_and_disposes_without_a_broker()
    {
        await using var transport = new RabbitMqMessagingProtocol(
            new RabbitMqProtocolOptions
            {
                ConnectionUri = new Uri("amqp://guest:guest@localhost:5672/"),
                EnableDeadLetter = false,
                ConfirmTimeout = TimeSpan.FromMilliseconds(1),
                ReadinessTimeout = TimeSpan.FromMilliseconds(1),
                NetworkRecoveryInterval = TimeSpan.FromMilliseconds(1),
                ShutdownTimeout = TimeSpan.FromMilliseconds(1)
            });

        Assert.Equal(
            TransportCapabilities.CompetingConsumers |
            TransportCapabilities.NativeHeaders,
            transport.Capabilities);
        Assert.Equal(TransportDeliveryGuarantee.AtLeastOnce, transport.Semantics.DeliveryGuarantee);
        Assert.Equal(TransportOrdering.PerChannel, transport.Semantics.Ordering);
        Assert.Equal(TransportDurability.Durable, transport.Semantics.Durability);
        Assert.True(transport.Semantics.SupportsBackpressure);

        var envelope = new TransportEnvelope(new MessageHeaders(), new byte[] { 1 });
        var handler = static (TransportEnvelope _, CancellationToken __) => ValueTask.CompletedTask;

        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.SendAsync(" ", envelope).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => transport.SendAsync("orders", null!).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.SubscribeAsync(" ", handler).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => transport.SubscribeAsync("orders", null!).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => transport.SubscribeAsync(
                "orders",
                handler,
                new SubscriptionOptions { MaxConcurrency = 0 }).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => transport.SendToDeadLetterAsync("orders", null!, new InvalidOperationException()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendToDeadLetterAsync("orders", envelope, new InvalidOperationException()).AsTask());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.CheckReadinessAsync(cancellation.Token).AsTask());

        await transport.DisposeAsync();
        await transport.DisposeAsync();

        var disposed = await transport.CheckReadinessAsync();
        Assert.Equal(TransportReadinessState.NotReady, disposed.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.SendAsync("orders", envelope).AsTask());
    }
}
