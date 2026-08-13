using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class ReplyInboxTests
{
    [Fact]
    public async Task One_subscription_routes_out_of_order_concurrent_correlations()
    {
        var transport = new InboxTransport();
        await using var inbox = new ReplyInbox(transport, "replies");
        var registrations = Enumerable.Range(0, 64)
            .Select(index => (CorrelationId: $"correlation-{index}", Completion: Completion()))
            .ToArray();

        await Task.WhenAll(registrations.Select(registration => inbox.RegisterAsync(
                registration.CorrelationId,
                registration.Completion,
                CancellationToken.None)
            .AsTask()));

        foreach (var registration in registrations.Reverse())
        {
            await transport.DeliverAsync(Reply(registration.CorrelationId));
        }

        await Task.WhenAll(registrations.Select(registration => registration.Completion.Task));
        Assert.Equal(1, transport.SubscribeCount);
    }

    [Fact]
    public async Task Late_unregister_does_not_remove_a_newer_registration()
    {
        var transport = new InboxTransport();
        await using var inbox = new ReplyInbox(transport, "replies");
        var first = Completion();

        await inbox.RegisterAsync("correlation", first, CancellationToken.None);
        inbox.Unregister("correlation", first);

        var second = Completion();
        await inbox.RegisterAsync("correlation", second, CancellationToken.None);
        inbox.Unregister("correlation", first);

        var reply = Reply("correlation");
        await transport.DeliverAsync(reply);

        Assert.Same(reply, await second.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Unknown_and_duplicate_replies_are_ignored()
    {
        var transport = new InboxTransport();
        await using var inbox = new ReplyInbox(transport, "replies");
        var completion = Completion();

        await inbox.RegisterAsync("known", completion, CancellationToken.None);
        await transport.DeliverAsync(Reply("unknown"));

        var reply = Reply("known");
        await transport.DeliverAsync(reply);
        await transport.DeliverAsync(reply);

        Assert.Same(reply, await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Disposal_completes_pending_operations_with_disposal_reason()
    {
        var transport = new InboxTransport();
        var inbox = new ReplyInbox(transport, "replies");
        var completion = Completion();

        await inbox.RegisterAsync("pending", completion, CancellationToken.None);
        await inbox.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => completion.Task);
    }

    private static TaskCompletionSource<TransportEnvelope> Completion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TransportEnvelope Reply(string correlationId)
        => new(
            new MessageHeaders
            {
                [HeaderNames.CorrelationId] = correlationId,
                [HeaderNames.MessageType] = MessageType.Response.ToString()
            },
            ReadOnlyMemory<byte>.Empty);

    private sealed class InboxTransport : ISubscriptionTransport
    {
        private Func<TransportEnvelope, CancellationToken, ValueTask>? _handler;
        private int _subscribeCount;

        public TransportCapabilities Capabilities => TransportCapabilities.None;

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public ValueTask<ITransportSubscription> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _subscribeCount);
            _handler = handler;
            return ValueTask.FromResult<ITransportSubscription>(new Subscription());
        }

        public ValueTask DeliverAsync(TransportEnvelope envelope)
            => _handler!(envelope, CancellationToken.None);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Subscription : ITransportSubscription
        {
            public ValueTask StopAcceptingAsync(CancellationToken cancellationToken = default)
                => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
