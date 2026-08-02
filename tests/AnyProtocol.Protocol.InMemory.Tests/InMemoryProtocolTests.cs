using System.Text;
using System.Runtime.InteropServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Conformance;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using Xunit;

namespace AnyProtocol.Protocol.InMemory.Tests;

public sealed class InMemoryConformanceTests : TransportConformanceTests
{
    protected override IMessagingProtocol CreateTransport() => new InMemoryMessagingProtocol();
}

public sealed class InMemoryRequestReplyTests
{
    [Fact]
    public async Task Fault_injector_fails_send_explicitly()
    {
        var expected = new InvalidOperationException("injected");
        await using var transport = new InMemoryMessagingProtocol(
            new InMemoryProtocolOptions { FaultInjector = (_, _) => expected });

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await transport.SendAsync(
                "events.fault",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty)));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Fan_out_deliveries_are_isolated_from_mutation()
    {
        await using var transport = new InMemoryMessagingProtocol();
        var firstReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondValue = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = await transport.SubscribeAsync(
            "events.isolated",
            (envelope, _) =>
            {
                envelope.Headers["value"] = "changed";
                MemoryMarshal.TryGetArray(envelope.Body, out var segment);
                segment.Array![segment.Offset] = 9;
                firstReceived.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var second = await transport.SubscribeAsync(
            "events.isolated",
            (envelope, _) =>
            {
                secondValue.TrySetResult($"{envelope.Headers["value"]}:{envelope.Body.Span[0]}");
                return ValueTask.CompletedTask;
            });
        var headers = new MessageHeaders { ["value"] = "original" };
        var body = new byte[] { 1 };

        await transport.SendAsync(
            "events.isolated",
            new TransportEnvelope(headers, body));
        await firstReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var observed = await secondValue.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("original:1", observed);
        Assert.Equal("original", headers["value"]);
        Assert.Equal(1, body[0]);
    }

    [Fact]
    public async Task Request_reply_correlates_response()
    {
        await using var transport = new InMemoryMessagingProtocol();
        await using var responder = await transport.SubscribeAsync(
            "orders.place",
            async (request, cancellationToken) =>
            {
                var responseHeaders = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await transport.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(responseHeaders, Encoding.UTF8.GetBytes("accepted")),
                    cancellationToken);
            });
        await using var engine = new RequestReplyEngine(transport);

        var response = await engine.RequestAsync(
            "orders.place",
            new TransportEnvelope(new MessageHeaders(), Encoding.UTF8.GetBytes("order-1")),
            TimeSpan.FromSeconds(2));

        Assert.Equal("accepted", Encoding.UTF8.GetString(response.Body.Span));
        Assert.Equal(MessageType.Response.ToString(), response.Headers[HeaderNames.MessageType]);
    }

    [Fact]
    public async Task Concurrent_requests_keep_correlations_isolated()
    {
        await using var transport = new InMemoryMessagingProtocol();
        await using var responder = await transport.SubscribeAsync(
            "orders.concurrent",
            async (request, cancellationToken) =>
            {
                var responseHeaders = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await transport.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(responseHeaders, request.Body),
                    cancellationToken);
            },
            new SubscriptionOptions { MaxConcurrency = 8 });
        await using var engine = new RequestReplyEngine(transport);

        var requests = Enumerable.Range(0, 100)
            .Select(async index =>
            {
                var response = await engine.RequestAsync(
                    "orders.concurrent",
                    new TransportEnvelope(
                        new MessageHeaders(),
                        Encoding.UTF8.GetBytes(index.ToString())),
                    TimeSpan.FromSeconds(5));
                return Encoding.UTF8.GetString(response.Body.Span);
            });

        var responses = await Task.WhenAll(requests);

        Assert.Equal(
            Enumerable.Range(0, 100).Select(index => index.ToString()).Order(),
            responses.Order());
    }

    [Fact]
    public async Task Request_reply_preserves_fault_envelope()
    {
        await using var transport = new InMemoryMessagingProtocol();
        await using var responder = await transport.SubscribeAsync(
            "orders.fail",
            async (request, cancellationToken) =>
            {
                var responseHeaders = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Fault.ToString()
                };
                await transport.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(responseHeaders, Encoding.UTF8.GetBytes("order-rejected")),
                    cancellationToken);
            });
        await using var engine = new RequestReplyEngine(transport);

        var response = await engine.RequestAsync(
            "orders.fail",
            new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
            TimeSpan.FromSeconds(2));

        Assert.Equal(MessageType.Fault.ToString(), response.Headers[HeaderNames.MessageType]);
        Assert.Equal("order-rejected", Encoding.UTF8.GetString(response.Body.Span));
    }

    [Fact]
    public async Task Request_times_out_when_no_responder_replies()
    {
        await using var transport = new InMemoryMessagingProtocol();
        await using var engine = new RequestReplyEngine(transport);

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await engine.RequestAsync(
                "orders.missing",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task Request_timeout_cancels_slow_transport_send()
    {
        await using var transport = new InMemoryMessagingProtocol(
            new InMemoryProtocolOptions { DeliveryDelay = TimeSpan.FromSeconds(2) });
        await using var engine = new RequestReplyEngine(transport);
        var startedAt = DateTime.UtcNow;

        await Assert.ThrowsAsync<TimeoutException>(
            async () => await engine.RequestAsync(
                "orders.slow",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromMilliseconds(30)));

        Assert.True(DateTime.UtcNow - startedAt < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Request_honors_cancellation()
    {
        await using var transport = new InMemoryMessagingProtocol();
        await using var engine = new RequestReplyEngine(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await engine.RequestAsync(
                "orders.cancel",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(2),
                cancellation.Token));
    }
}
