using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.ZeroMq.Tests;

public sealed class ZeroMqMessagingProtocolTests
{
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

    private static (ZeroMqMessagingProtocol Server, ZeroMqMessagingProtocol Client) CreatePair()
    {
        var endpoints = CreateEndpoints();
        return (CreateServer(endpoints), CreateClient(endpoints));
    }

    private static ZeroMqMessagingProtocol CreateServer(Endpoints endpoints)
        => new(
            new ZeroMqProtocolOptions
            {
                Role = ZeroMqRole.Server,
                RouterEndpoint = endpoints.Router,
                PublisherEndpoint = endpoints.Publisher
            });

    private static ZeroMqMessagingProtocol CreateClient(Endpoints endpoints)
        => new(
            new ZeroMqProtocolOptions
            {
                Role = ZeroMqRole.Client,
                RouterEndpoint = endpoints.Router,
                PublisherEndpoint = endpoints.Publisher
            });

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
}
