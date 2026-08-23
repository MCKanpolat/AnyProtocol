using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Encoder.Compression;
using AnyProtocol.Encoder.MessagePack;
using AnyProtocol.Encoder.Protobuf;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.ZeroMq.Tests;

public sealed class ZeroMqMessagingProtocolTests
{
    [Fact]
    public void Protocol_options_validate_endpoints_watermarks_and_poll_interval()
    {
        var endpoints = CreateEndpoints();

        Assert.Throws<ArgumentNullException>(
            () => new ZeroMqMessagingProtocol(
                new ZeroMqProtocolOptions
                {
                    Role = ZeroMqRole.Server,
                    RouterEndpoint = null!,
                    PublisherEndpoint = endpoints.Publisher
                }));
        Assert.Throws<ArgumentException>(
            () => new ZeroMqMessagingProtocol(
                new ZeroMqProtocolOptions
                {
                    Role = ZeroMqRole.Server,
                    RouterEndpoint = endpoints.Router,
                    PublisherEndpoint = " "
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZeroMqMessagingProtocol(
                new ZeroMqProtocolOptions
                {
                    Role = ZeroMqRole.Server,
                    RouterEndpoint = endpoints.Router,
                    PublisherEndpoint = endpoints.Publisher,
                    HighWatermark = 0
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZeroMqMessagingProtocol(
                new ZeroMqProtocolOptions
                {
                    Role = ZeroMqRole.Server,
                    RouterEndpoint = endpoints.Router,
                    PublisherEndpoint = endpoints.Publisher,
                    PollInterval = TimeSpan.FromMilliseconds(-1)
                }));
    }

    [Fact]
    public void Subscription_queue_capacity_must_be_positive()
    {
        var endpoints = CreateEndpoints();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ZeroMqMessagingProtocol(
                new ZeroMqProtocolOptions
                {
                    Role = ZeroMqRole.Server,
                    RouterEndpoint = endpoints.Router,
                    PublisherEndpoint = endpoints.Publisher,
                    SubscriptionQueueCapacity = 0
                }));

        Assert.Equal("SubscriptionQueueCapacity", exception.ParamName);
    }

    [Fact]
    public async Task Validation_readiness_and_disposal_paths_are_deterministic()
    {
        var endpoints = CreateEndpoints();
        await using var server = CreateServer(endpoints);
        var envelope = new TransportEnvelope(new MessageHeaders(), new byte[] { 1 });
        var handler = static (TransportEnvelope _, CancellationToken __) => ValueTask.CompletedTask;

        Assert.Equal(
            TransportReadinessState.Ready,
            (await server.CheckReadinessAsync()).State);
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.SendAsync(" ", envelope).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => server.SendAsync("orders", null!).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(
            () => server.SubscribeAsync(" ", handler).AsTask());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => server.SubscribeAsync("orders", null!).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => server.SubscribeAsync(
                "orders",
                handler,
                new SubscriptionOptions { MaxConcurrency = 0 }).AsTask());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.SubscribeAsync("orders", handler, cancellationToken: cancellation.Token).AsTask());

        await server.DisposeAsync();
        await server.DisposeAsync();

        Assert.Equal(
            TransportReadinessState.NotReady,
            (await server.CheckReadinessAsync()).State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => server.SendAsync("orders", envelope).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => server.SubscribeAsync("orders", handler).AsTask());
    }

    [Fact]
    public async Task Stop_accepting_unblocks_a_receive_loop_waiting_on_a_full_subscription_queue()
    {
        var endpoints = CreateEndpoints();
        await using var server = CreateServer(
            endpoints,
            subscriptionQueueCapacity: 1);
        await using var client = CreateClient(endpoints);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await server.SubscribeAsync(
            "events.backpressure",
            async (_, cancellationToken) =>
            {
                handlerStarted.TrySetResult();
                await releaseHandler.Task.WaitAsync(cancellationToken);
            });

        await Task.Delay(250);
        for (var index = 0; index < 3; index++)
        {
            await client.SendAsync(
                "events.backpressure",
                new TransportEnvelope(new MessageHeaders(), new byte[] { (byte)index }));
        }

        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        await subscription.StopAcceptingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        releaseHandler.TrySetResult();
    }
    [Fact]
    public async Task Client_to_server_preserves_headers_and_body()
    {
        var (server, client) = CreatePair();
        await using var serverLifetime = server;
        await using var clientLifetime = client;
        var received = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await server.SubscribeAsync(
            "orders",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });

        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = "message-1",
            [HeaderNames.TraceParent] = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01",
            ["x-custom"] = "value"
        };
        await client.SendAsync(
            "orders",
            new TransportEnvelope(headers, Encoding.UTF8.GetBytes("payload")));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("message-1", envelope.Headers[HeaderNames.MessageId]);
        Assert.Equal("value", envelope.Headers["x-custom"]);
        Assert.Equal("payload", Encoding.UTF8.GetString(envelope.Body.Span));
    }

    [Fact]
    public async Task Client_and_server_can_use_a_custom_envelope_codec()
    {
        var codec = new PrefixEnvelopeCodec();
        var (server, client) = CreatePair(codec);
        await using var serverLifetime = server;
        await using var clientLifetime = client;
        var received = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await server.SubscribeAsync(
            "custom",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });

        await client.SendAsync(
            "custom",
            new TransportEnvelope(new MessageHeaders(), "custom-body"u8.ToArray()));

        Assert.Equal("custom-body", Encoding.UTF8.GetString(
            (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Body.Span));
        Assert.True(codec.EncodeCalls > 0);
        Assert.True(codec.DecodeCalls > 0);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("messagepack")]
    [InlineData("protobuf")]
    [InlineData("compressed-binary")]
    [InlineData("compressed-messagepack")]
    [InlineData("compressed-protobuf")]
    [InlineData("brotli-compressed-binary")]
    [InlineData("brotli-compressed-messagepack")]
    [InlineData("brotli-compressed-protobuf")]
    public async Task Client_to_server_round_trips_with_each_envelope_codec(string codecName)
    {
        var (server, client) = CreatePair(CreateCodec(codecName));
        await using var serverLifetime = server;
        await using var clientLifetime = client;
        var received = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await server.SubscribeAsync(
            "codec-matrix",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });

        await client.SendAsync(
            "codec-matrix",
            new TransportEnvelope(new MessageHeaders(), "codec-matrix"u8.ToArray()));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("codec-matrix", Encoding.UTF8.GetString(envelope.Body.Span));
    }

    [Fact]
    public async Task Request_reply_uses_dealer_router_and_pub_sub_paths()
    {
        var (server, client) = CreatePair();
        await using var serverLifetime = server;
        await using var clientLifetime = client;
        await using var subscription = await server.SubscribeAsync(
            "echo",
            async (request, cancellationToken) =>
            {
                var responseHeaders = new MessageHeaders
                {
                    [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.MessageId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await server.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(responseHeaders, request.Body),
                    cancellationToken);
            });
        await using var engine = new RequestReplyEngine(client);

        // NetMQ PUB/SUB intentionally has slow-joiner semantics.
        await Task.Delay(250);
        var response = await engine.RequestAsync(
            "echo",
            new TransportEnvelope(new MessageHeaders(), Encoding.UTF8.GetBytes("hello")),
            TimeSpan.FromSeconds(5));

        Assert.Equal("hello", Encoding.UTF8.GetString(response.Body.Span));
        Assert.Equal(MessageType.Response, response.Headers.Get(
            HeaderNames.MessageType,
            MessageType.Fault));
    }

    [Fact]
    public async Task Server_publication_fans_out_to_clients()
    {
        var endpoints = CreateEndpoints();
        await using var server = CreateServer(endpoints);
        await using var first = CreateClient(endpoints);
        await using var second = CreateClient(endpoints);
        var firstReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstSubscription = await first.SubscribeAsync(
            "events",
            (_, _) =>
            {
                firstReceived.TrySetResult();
                return ValueTask.CompletedTask;
            });
        await using var secondSubscription = await second.SubscribeAsync(
            "events",
            (_, _) =>
            {
                secondReceived.TrySetResult();
                return ValueTask.CompletedTask;
            });

        await Task.Delay(250);
        await server.SendAsync(
            "events",
            new TransportEnvelope(new MessageHeaders(), new byte[] { 1, 2, 3 }));

        await Task.WhenAll(firstReceived.Task, secondReceived.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Consumer_group_selects_one_local_handler_per_message()
    {
        var (server, client) = CreatePair();
        await using var serverLifetime = server;
        await using var clientLifetime = client;
        var count = 0;
        var completed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(TransportEnvelope _, CancellationToken __)
        {
            if (Interlocked.Increment(ref count) == 10)
            {
                completed.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        var options = new SubscriptionOptions { ConsumerGroup = "workers" };
        await using var first = await server.SubscribeAsync("jobs", Handler, options);
        await using var second = await server.SubscribeAsync("jobs", Handler, options);

        for (var index = 0; index < 10; index++)
        {
            await client.SendAsync(
                "jobs",
                new TransportEnvelope(
                    new MessageHeaders(),
                    new byte[] { checked((byte)index) }));
        }

        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(10, Volatile.Read(ref count));
    }

    private static (ZeroMqMessagingProtocol Server, ZeroMqMessagingProtocol Client) CreatePair(
        IEnvelopeCodec? codec = null)
    {
        var endpoints = CreateEndpoints();
        return (CreateServer(endpoints, codec), CreateClient(endpoints, codec));
    }

    private static IEnvelopeCodec CreateCodec(string name)
        => name switch
        {
            "binary" => new BinaryEnvelopeCodec(),
            "messagepack" => new MessagePackEnvelopeCodec(),
            "protobuf" => new ProtobufEnvelopeCodec(),
            "compressed-binary" => Compress(new BinaryEnvelopeCodec()),
            "compressed-messagepack" => Compress(new MessagePackEnvelopeCodec()),
            "compressed-protobuf" => Compress(new ProtobufEnvelopeCodec()),
            "brotli-compressed-binary" => Compress(
                new BinaryEnvelopeCodec(),
                CompressionAlgorithm.Brotli),
            "brotli-compressed-messagepack" => Compress(
                new MessagePackEnvelopeCodec(),
                CompressionAlgorithm.Brotli),
            "brotli-compressed-protobuf" => Compress(
                new ProtobufEnvelopeCodec(),
                CompressionAlgorithm.Brotli),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown test codec.")
        };

    private static IEnvelopeCodec Compress(
        IEnvelopeCodec inner,
        CompressionAlgorithm algorithm = CompressionAlgorithm.GZip)
        => new CompressedEnvelopeCodec(
            inner,
            new CompressedEnvelopeCodecOptions
            {
                Algorithm = algorithm,
                CompressionThreshold = 0
            });

    private static ZeroMqMessagingProtocol CreateServer(
        Endpoints endpoints,
        IEnvelopeCodec? codec = null,
        int? subscriptionQueueCapacity = null)
    {
        var options = new ZeroMqProtocolOptions
        {
            Role = ZeroMqRole.Server,
            RouterEndpoint = endpoints.Router,
            PublisherEndpoint = endpoints.Publisher,
            SubscriptionQueueCapacity = subscriptionQueueCapacity ?? 1000
        };
        return new ZeroMqMessagingProtocol(options, codec ?? new BinaryEnvelopeCodec());
    }

    private static ZeroMqMessagingProtocol CreateClient(Endpoints endpoints, IEnvelopeCodec? codec = null)
        => new(
            new ZeroMqProtocolOptions
            {
                Role = ZeroMqRole.Client,
                RouterEndpoint = endpoints.Router,
                PublisherEndpoint = endpoints.Publisher
            },
            codec ?? new BinaryEnvelopeCodec());

    private static Endpoints CreateEndpoints()
    {
        var routerPort = GetFreePort();
        int publisherPort;
        do
        {
            publisherPort = GetFreePort();
        }
        while (publisherPort == routerPort);

        return new Endpoints(
            $"tcp://127.0.0.1:{routerPort}",
            $"tcp://127.0.0.1:{publisherPort}");
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record Endpoints(string Router, string Publisher);

    private sealed class PrefixEnvelopeCodec : IEnvelopeCodec
    {
        private const byte Prefix = 0xA5;
        private readonly BinaryEnvelopeCodec _inner = new();

        public int EncodeCalls { get; private set; }

        public int DecodeCalls { get; private set; }

        public ReadOnlyMemory<byte> Encode(TransportEnvelope envelope)
        {
            EncodeCalls++;
            var frame = _inner.Encode(envelope);
            var result = new byte[frame.Length + 1];
            result[0] = Prefix;
            frame.Span.CopyTo(result.AsSpan(1));
            return result;
        }

        public TransportEnvelope Decode(ReadOnlyMemory<byte> frame)
        {
            DecodeCalls++;
            if (frame.Length == 0 || frame.Span[0] != Prefix)
            {
                throw new InvalidDataException("The test envelope prefix is invalid.");
            }

            return _inner.Decode(frame[1..]);
        }
    }
}
