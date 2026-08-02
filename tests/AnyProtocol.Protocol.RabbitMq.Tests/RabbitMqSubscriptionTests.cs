using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqSubscriptionTests(RabbitMqFixture fixture)
    : IClassFixture<RabbitMqFixture>
{
    [SkippableFact]
    public async Task Max_concurrency_allows_four_handlers_to_be_active()
    {
        fixture.RequireRabbitMq();
        await using var transport = fixture.CreateTransport();
        var fourActive = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        async ValueTask Handle(TransportEnvelope _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref active) == 4)
            {
                fourActive.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
        }

        await using var subscription = await transport.SubscribeAsync(
            "orders.concurrent",
            Handle,
            new SubscriptionOptions
            {
                ConsumerGroup = $"workers-{Guid.NewGuid():N}",
                MaxConcurrency = 4
            });

        for (var index = 0; index < 4; index++)
        {
            await transport.SendAsync(
                "orders.concurrent",
                new TransportEnvelope(new MessageHeaders(), new byte[] { (byte)index }));
        }

        await fourActive.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(4, Volatile.Read(ref active));
        release.TrySetResult();
    }

    [SkippableFact]
    public async Task Concurrency_one_waits_for_handler_before_starting_next_delivery()
    {
        fixture.RequireRabbitMq();
        await using var transport = fixture.CreateTransport();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        async ValueTask Handle(TransportEnvelope _, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task.WaitAsync(cancellationToken);
                return;
            }

            secondStarted.TrySetResult();
        }

        await using var subscription = await transport.SubscribeAsync(
            "orders.sequential",
            Handle,
            new SubscriptionOptions
            {
                ConsumerGroup = $"workers-{Guid.NewGuid():N}",
                MaxConcurrency = 1
            });
        await transport.SendAsync(
            "orders.sequential",
            new TransportEnvelope(new MessageHeaders(), new byte[] { 1 }));
        await transport.SendAsync(
            "orders.sequential",
            new TransportEnvelope(new MessageHeaders(), new byte[] { 2 }));

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(250);
        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
