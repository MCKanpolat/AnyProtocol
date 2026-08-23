using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaProtocolValidationTests
{
    [Fact]
    public async Task Protocol_exposes_semantics_and_validates_inputs_without_a_broker()
    {
        await using var transport = new KafkaMessagingProtocol(
            new KafkaProtocolOptions
            {
                BootstrapServers = "localhost:9092",
                ClientId = "coverage-kafka",
                AutoCreateTopics = false,
                EnableDeadLetter = false,
                ProducerFlushTimeout = TimeSpan.FromMilliseconds(1),
                ProducerConfig = new Dictionary<string, string>
                {
                    ["client.id"] = "coverage-kafka-producer"
                }
            });

        Assert.Equal(
            TransportCapabilities.CompetingConsumers |
            TransportCapabilities.NativeHeaders,
            transport.Capabilities);
        Assert.Equal(TransportDeliveryGuarantee.AtLeastOnce, transport.Semantics.DeliveryGuarantee);
        Assert.Equal(TransportOrdering.PerPartition, transport.Semantics.Ordering);
        Assert.Equal(TransportDurability.Durable, transport.Semantics.Durability);
        Assert.True(transport.Semantics.SupportsPartitioning);
        Assert.True(transport.Semantics.SupportsBackpressure);

        var envelope = new TransportEnvelope(new MessageHeaders(), new byte[] { 1 });
        var handler = static (TransportEnvelope _, CancellationToken __) => ValueTask.CompletedTask;

        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.SendAsync(" ", envelope).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => transport.SendAsync("orders", null!).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => transport.SendAsync("orders/invalid", envelope).AsTask());
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
