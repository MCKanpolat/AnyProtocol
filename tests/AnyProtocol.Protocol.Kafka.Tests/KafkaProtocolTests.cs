using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Conformance;
using AnyProtocol.Protocol.Abstraction;
using Confluent.Kafka;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaConformanceTests(KafkaFixture fixture)
    : TransportConformanceTests, IClassFixture<KafkaFixture>
{
    protected override TimeSpan Timeout => TimeSpan.FromSeconds(20);

    protected override IMessagingProtocol CreateTransport() => fixture.CreateTransport();
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

        Assert.False(received.Headers.ContainsKey("x-test"));
        Assert.Equal(
            "handler_failed",
            received.Headers[HeaderNames.DeadLetterError]);
        Assert.EndsWith(
            ".events.failed",
            received.Headers[HeaderNames.DeadLetterSource],
            StringComparison.Ordinal);
        Assert.Empty(received.Body.ToArray());
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
