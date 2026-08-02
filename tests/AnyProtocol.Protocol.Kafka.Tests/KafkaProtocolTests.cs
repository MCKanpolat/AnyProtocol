using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Confluent.Kafka;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaConformanceTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    [SkippableFact]
    public async Task Send_and_subscribe_preserve_body_and_headers()
    {
        fixture.RequireKafka();
        await using var transport = fixture.CreateTransport();
        var received = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await transport.SubscribeAsync(
            "conformance.roundtrip",
            (envelope, _) =>
            {
                received.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });

        await transport.SendAsync(
            "conformance.roundtrip",
            new TransportEnvelope(
                new MessageHeaders
                {
                    [HeaderNames.MessageId] = "message-1",
                    ["x-custom"] = "custom-value"
                },
                new byte[] { 1, 2, 3 }));
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("message-1", envelope.Headers[HeaderNames.MessageId]);
        Assert.Equal("custom-value", envelope.Headers["X-CUSTOM"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, envelope.Body.ToArray());
    }

    [SkippableFact]
    public async Task Publish_subscribe_fans_out_to_independent_subscribers()
    {
        fixture.RequireKafka();
        await using var transport = fixture.CreateTransport();
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

        await Task.WhenAll(first.Task, second.Task).WaitAsync(TimeSpan.FromSeconds(20));
    }

    [SkippableFact]
    public async Task Consumer_group_delivers_each_message_once()
    {
        fixture.RequireKafka();
        await using var transport = fixture.CreateTransport();
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

        var options = new SubscriptionOptions { ConsumerGroup = $"workers-{Guid.NewGuid():N}" };
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

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(500);
        Assert.Equal(20, Volatile.Read(ref received));
    }
}

public sealed class KafkaProtocolTests(KafkaFixture fixture) : IClassFixture<KafkaFixture>
{
    [SkippableFact]
    public async Task Request_reply_channels_are_isolated_per_client()
    {
        fixture.RequireKafka();
        var prefix = $"rpc-{Guid.NewGuid():N}";
        await using var server = fixture.CreateTransport(prefix);
        await using var firstClient = fixture.CreateTransport(prefix);
        await using var secondClient = fixture.CreateTransport(prefix);
        await using var responder = await server.SubscribeAsync(
            "orders.lookup",
            async (request, cancellationToken) =>
            {
                var responseHeaders = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await server.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(responseHeaders, request.Body),
                    cancellationToken);
            },
            new SubscriptionOptions { ConsumerGroup = $"{prefix}.servers" });
        await using var firstEngine = new RequestReplyEngine(firstClient);
        await using var secondEngine = new RequestReplyEngine(secondClient);

        var results = new List<string>();
        for (var index = 0; index < 10; index++)
        {
            results.AddRange(
                await Task.WhenAll(
                    RequestAsync(firstEngine, $"first-{index}"),
                    RequestAsync(secondEngine, $"second-{index}")));
        }

        Assert.Equal(20, results.Distinct(StringComparer.Ordinal).Count());
    }

    [SkippableFact]
    public async Task Failed_handler_publishes_original_message_to_dead_letter_topic()
    {
        fixture.RequireKafka();
        var prefix = $"dead-{Guid.NewGuid():N}";
        await using var transport = fixture.CreateTransport(prefix);
        var deadLetter = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var deadLetterSubscription = await transport.SubscribeAsync(
            "events.failed.dead-letter",
            (envelope, _) =>
            {
                deadLetter.TrySetResult(envelope);
                return ValueTask.CompletedTask;
            });
        await using var sourceSubscription = await transport.SubscribeAsync(
            "events.failed",
            (_, _) => ValueTask.FromException(new InvalidOperationException("handler failed")));
        var original = new TransportEnvelope(
            new MessageHeaders { ["x-test"] = "preserved" },
            Encoding.UTF8.GetBytes("payload"));

        await transport.SendAsync("events.failed", original);
        var received = await deadLetter.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("preserved", received.Headers["x-test"]);
        Assert.Equal(
            "Handler execution failed.",
            received.Headers[HeaderNames.DeadLetterError]);
        Assert.EndsWith(
            ".events.failed",
            received.Headers[HeaderNames.DeadLetterSource],
            StringComparison.Ordinal);
        Assert.Equal("payload", Encoding.UTF8.GetString(received.Body.Span));
    }

    [SkippableFact]
    public async Task Partition_key_is_used_as_Kafka_key_and_preserves_partition_order()
    {
        fixture.RequireKafka();
        var prefix = $"keys-{Guid.NewGuid():N}";
        var channel = "orders.partitioned";
        var topic = $"{prefix}.{channel}";
        await using var transport = fixture.CreateTransport(prefix);
        await using var readiness = await transport.SubscribeAsync(
            channel,
            (_, _) => ValueTask.CompletedTask);

        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = fixture.BootstrapServers,
            GroupId = $"inspect-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        };
        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Subscribe(topic);

        for (var index = 0; index < 10; index++)
        {
            await transport.SendAsync(
                channel,
                new TransportEnvelope(
                    new MessageHeaders { [HeaderNames.PartitionKey] = "customer-42" },
                    Encoding.UTF8.GetBytes(index.ToString())));
        }

        var consumed = new List<ConsumeResult<string, byte[]>>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (consumed.Count < 10)
        {
            consumed.Add(consumer.Consume(timeout.Token));
        }

        Assert.All(consumed, result => Assert.Equal("customer-42", result.Message.Key));
        Assert.Single(consumed.Select(result => result.Partition.Value).Distinct());
        Assert.Equal(
            Enumerable.Range(0, 10).Select(index => index.ToString()),
            consumed.Select(result => Encoding.UTF8.GetString(result.Message.Value)));
    }

    [SkippableFact]
    public async Task In_flight_delivery_completes_during_consumer_group_rebalance()
    {
        fixture.RequireKafka();
        var prefix = $"rebalance-{Guid.NewGuid():N}";
        await using var transport = fixture.CreateTransport(prefix);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;
        async ValueTask HandleFirst(TransportEnvelope _, CancellationToken cancellationToken)
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            if (Interlocked.Increment(ref handled) == 2)
            {
                allHandled.TrySetResult();
            }
        }

        ValueTask HandleSecond(TransportEnvelope _, CancellationToken __)
        {
            if (Interlocked.Increment(ref handled) == 2)
            {
                allHandled.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }

        var options = new SubscriptionOptions { ConsumerGroup = $"{prefix}.workers" };
        await using var first = await transport.SubscribeAsync(
            "orders.rebalance",
            HandleFirst,
            options);
        await transport.SendAsync(
            "orders.rebalance",
            new TransportEnvelope(new MessageHeaders(), Encoding.UTF8.GetBytes("first")));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var secondTask = transport.SubscribeAsync("orders.rebalance", HandleSecond, options).AsTask();
        await Task.Delay(500);
        releaseFirst.TrySetResult();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(20));
        await transport.SendAsync(
            "orders.rebalance",
            new TransportEnvelope(new MessageHeaders(), Encoding.UTF8.GetBytes("second")));

        await allHandled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, Volatile.Read(ref handled));
    }

    private static async Task<string> RequestAsync(RequestReplyEngine engine, string value)
    {
        var response = await engine.RequestAsync(
            "orders.lookup",
            new TransportEnvelope(new MessageHeaders(), Encoding.UTF8.GetBytes(value)),
            TimeSpan.FromSeconds(20));
        return Encoding.UTF8.GetString(response.Body.Span);
    }
}
