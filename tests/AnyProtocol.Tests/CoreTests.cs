using System.Buffers.Binary;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
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
    public void Binary_codec_preserves_the_v1_empty_envelope_fixture()
    {
        var codec = new BinaryEnvelopeCodec();

        var frame = codec.Encode(new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));

        Assert.Equal(
            Convert.FromHexString("434C4E4B010000000000000000"),
            frame.ToArray());
    }

    [Fact]
    public void Binary_codec_enforces_limits_and_rejects_malformed_frames()
    {
        var envelope = new TransportEnvelope(
            new MessageHeaders { ["Ünicode"] = "значение" },
            new byte[] { 1, 2, 3 });
        var codec = new BinaryEnvelopeCodec(
            new EnvelopeCodecLimits
            {
                MaxFrameSize = 128,
                MaxBodySize = 3,
                MaxHeaderCount = 2,
                MaxHeaderBytes = 64
            });
        var frame = codec.Encode(envelope).ToArray();

        Assert.Equal("значение", codec.Decode(frame).Headers["ünicode"]);
        Assert.Throws<InvalidDataException>(() => codec.Decode(frame[..^1]));
        var trailing = new byte[frame.Length + 1];
        frame.CopyTo(trailing, 0);
        Assert.Throws<InvalidDataException>(() => codec.Decode(trailing));

        var invalidMagic = frame.ToArray();
        invalidMagic[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidMagic));

        var invalidVersion = frame.ToArray();
        invalidVersion[4] = 2;
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidVersion));

        var invalidBodyLength = frame.ToArray();
        var bodyLengthOffset = 9 + 4 + Encoding.UTF8.GetByteCount("Ünicode") + 4 +
                               Encoding.UTF8.GetByteCount("значение");
        BinaryPrimitives.WriteInt32LittleEndian(invalidBodyLength.AsSpan(bodyLengthOffset, sizeof(int)), -1);
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidBodyLength));

        var invalidHeaderCount = frame.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(invalidHeaderCount.AsSpan(5, sizeof(int)), 3);
        Assert.Throws<InvalidDataException>(() => codec.Decode(invalidHeaderCount));
    }

    [Fact]
    public void Binary_codec_rejects_duplicate_case_insensitive_headers()
    {
        var frame = new List<byte>();
        frame.AddRange(Convert.FromHexString("434C4E4B0102000000"));
        AddString(frame, "Header");
        AddString(frame, "one");
        AddString(frame, "header");
        AddString(frame, "two");
        frame.AddRange(new byte[4]);

        Assert.Throws<InvalidDataException>(() => new BinaryEnvelopeCodec().Decode(frame.ToArray()));

        static void AddString(List<byte> destination, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var length = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            destination.AddRange(length);
            destination.AddRange(bytes);
        }
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
    public void Message_context_does_not_expose_runtime_service_or_result_state()
    {
        var properties = typeof(IMessageContext).GetProperties();

        Assert.DoesNotContain(properties, property => property.Name == "Services");
        Assert.DoesNotContain(properties, property => property.Name == "Response");
        Assert.DoesNotContain(properties, property => property.Name == "Exception");
        Assert.False(
            properties.Single(property => property.Name == nameof(IMessageContext.CancellationToken))
                .CanWrite);
    }

    [Fact]
    public async Task Timeout_filter_scopes_cancellation_without_mutating_the_original_context()
    {
        using var callerCancellation = new CancellationTokenSource();
        var context = new MessageContext(
            new MessageHeaders(),
            ReadOnlyMemory<byte>.Empty,
            "orders",
            MessageType.Request,
            MessageDirection.Outbound,
            callerCancellation.Token);
        var observedToken = default(CancellationToken);

        await new TimeoutFilter(TimeSpan.FromSeconds(1)).InvokeAsync(
            context,
            current =>
            {
                observedToken = current.CancellationToken;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(callerCancellation.Token, context.CancellationToken);
        Assert.NotEqual(callerCancellation.Token, observedToken);
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

    private sealed class OutOfOrderStreamTransport : ISendTransport, ISubscriptionTransport
    {
        private Func<TransportEnvelope, CancellationToken, ValueTask>? _replyHandler;

        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

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

        public ValueTask<ITransportSubscription> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _replyHandler = handler;
            return ValueTask.FromResult<ITransportSubscription>(new Subscription());
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

        private sealed class Subscription : ITransportSubscription
        {
            public ValueTask StopAcceptingAsync(CancellationToken cancellationToken = default)
                => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
