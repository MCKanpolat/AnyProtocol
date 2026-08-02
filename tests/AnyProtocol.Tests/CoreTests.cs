using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class CoreTests
{
    [Fact]
    public void Protocol_keys_are_case_insensitive_and_reject_blank_values()
    {
        var upper = ProtocolKey.Create("ORDERS-REST");
        var lower = ProtocolKey.Create("orders-rest");

        Assert.Equal(upper, lower);
        Assert.Equal("orders-rest", lower.Value);
        Assert.Equal(ProtocolKey.Rest, ProtocolKey.Create("REST"));
        Assert.Throws<ArgumentException>(() => ProtocolKey.Create(" "));
    }

    [Fact]
    public void Headers_are_case_insensitive_and_support_typed_values()
    {
        var headers = new MessageHeaders();

        headers.Set(HeaderNames.MessageId, "message-1");
        headers.Set(HeaderNames.StreamSequence, 42);

        Assert.Equal("message-1", headers["CL-MESSAGE-ID"]);
        Assert.Equal(42, headers.Get<int>("CL-STREAM-SEQ"));
    }

    [Fact]
    public void Binary_codec_round_trips_headers_and_body()
    {
        var codec = new BinaryEnvelopeCodec();
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = "message-1",
            [HeaderNames.ContentType] = "application/json"
        };
        var envelope = new TransportEnvelope(headers, Encoding.UTF8.GetBytes("""{"ok":true}"""));

        var decoded = codec.Decode(codec.Encode(envelope));

        Assert.Equal("message-1", decoded.Headers[HeaderNames.MessageId]);
        Assert.Equal(envelope.Body.ToArray(), decoded.Body.ToArray());
    }

    [Fact]
    public async Task Pipeline_wraps_terminal_in_registration_order()
    {
        var calls = new List<string>();
        var pipeline = PipelineBuilder.Build(
            [new RecordingFilter("first", calls), new RecordingFilter("second", calls)],
            _ =>
            {
                calls.Add("terminal");
                return ValueTask.CompletedTask;
            });
        var context = new MessageContext(
            new MessageHeaders(),
            ReadOnlyMemory<byte>.Empty,
            "orders",
            MessageType.Event,
            MessageDirection.Outbound);

        await pipeline(context);

        Assert.Equal(
            ["first:before", "second:before", "terminal", "second:after", "first:after"],
            calls);
    }

    [Fact]
    public async Task Stream_engine_rejects_out_of_order_items()
    {
        await using var transport = new OutOfOrderStreamTransport();
        await using var engine = new StreamEngine(transport, capacity: 4);
        await using var enumerator = engine.StreamAsync(
                "orders.stream",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                TimeSpan.FromSeconds(2))
            .GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await enumerator.MoveNextAsync().AsTask());

        Assert.Contains("expected sequence 1", exception.Message);
    }

    private sealed class RecordingFilter(string name, ICollection<string> calls) : IMessageFilter
    {
        public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
        {
            calls.Add($"{name}:before");
            await next(context);
            calls.Add($"{name}:after");
        }
    }

    private sealed class OutOfOrderStreamTransport : IMessagingProtocol
    {
        private Func<TransportEnvelope, CancellationToken, ValueTask>? _replyHandler;

        public TransportCapabilities Capabilities =>
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.NativeHeaders;

        public async ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            var correlationId = envelope.Headers[HeaderNames.CorrelationId];
            await _replyHandler!(
                CreateItem(correlationId, 0),
                cancellationToken);
            await _replyHandler(
                CreateItem(correlationId, 2),
                cancellationToken);
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _replyHandler = handler;
            return ValueTask.FromResult<IAsyncDisposable>(new Subscription());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TransportEnvelope CreateItem(string? correlationId, long sequence)
            => new(
                new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = correlationId,
                    [HeaderNames.MessageType] = MessageType.StreamItem.ToString(),
                    [HeaderNames.StreamSequence] = sequence.ToString()
                },
                ReadOnlyMemory<byte>.Empty);

        private sealed class Subscription : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
