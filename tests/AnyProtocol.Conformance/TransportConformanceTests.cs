using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Xunit;

namespace AnyProtocol.Conformance;

public abstract class TransportConformanceTests
{
    protected abstract IMessagingProtocol CreateTransport();

    protected virtual TimeSpan Timeout => TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Send_and_subscribe_preserve_body_and_headers()
    {
        await using var transport = CreateTransport();
        var received = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await transport.SubscribeAsync(
            "conformance.roundtrip",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = "message-1",
            ["x-custom"] = "custom-value"
        };

        await transport.SendAsync(
            "conformance.roundtrip",
            new TransportEnvelope(headers, new byte[] { 1, 2, 3 }));
        var envelope = await received.Task.WaitAsync(Timeout);

        Assert.Equal("message-1", envelope.Headers[HeaderNames.MessageId]);
        Assert.Equal("custom-value", envelope.Headers["X-CUSTOM"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, envelope.Body.ToArray());
    }

    [Fact]
    public async Task Publish_subscribe_fans_out_to_independent_subscribers()
    {
        await using var transport = CreateTransport();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstSubscription = await transport.SubscribeAsync(
            "conformance.fanout",
            (_, _) =>
            {
                first.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var secondSubscription = await transport.SubscribeAsync(
            "conformance.fanout",
            (_, _) =>
            {
                second.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await transport.SendAsync(
            "conformance.fanout",
            new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));

        await Task.WhenAll(first.Task, second.Task).WaitAsync(Timeout);
    }

    [Fact]
    public async Task Consumer_group_delivers_each_message_once()
    {
        await using var transport = CreateTransport();
        var received = 0;
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handle(TransportEnvelope _, CancellationToken __)
        {
            if (Interlocked.Increment(ref received) == 20)
            {
                allReceived.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        var options = new SubscriptionOptions { ConsumerGroup = "workers" };
        await using var firstSubscription =
            await transport.SubscribeAsync("conformance.group", Handle, options);
        await using var secondSubscription =
            await transport.SubscribeAsync("conformance.group", Handle, options);

        for (var index = 0; index < 20; index++)
        {
            await transport.SendAsync(
                "conformance.group",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));
        }

        await allReceived.Task.WaitAsync(Timeout);
        await Task.Delay(50);
        Assert.Equal(20, Volatile.Read(ref received));
    }
}
