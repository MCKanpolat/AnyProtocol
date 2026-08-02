using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using RabbitMQ.Client;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqRetryTests(RabbitMqFixture fixture)
    : IClassFixture<RabbitMqFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [SkippableFact]
    public async Task Transient_failure_republishes_with_incremented_delivery_attempt()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions() with { MaxDeliveryAttempts = 3 };
        await using var transport = new RabbitMqMessagingProtocol(options);
        var attempts = new List<string?>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await transport.SubscribeAsync(
            "orders.retry",
            (envelope, _) =>
            {
                lock (attempts)
                {
                    attempts.Add(envelope.Headers[RabbitMqEnvelopeMapper.DeliveryAttemptHeader]);
                    if (attempts.Count < 3)
                    {
                        throw new InvalidOperationException("transient");
                    }
                }

                completed.TrySetResult();
                return ValueTask.CompletedTask;
            },
            new SubscriptionOptions { ConsumerGroup = $"workers-{Guid.NewGuid():N}" });

        await transport.SendAsync(
            "orders.retry",
            new TransportEnvelope(new MessageHeaders(), "retry"u8.ToArray()));
        await completed.Task.WaitAsync(Timeout);

        Assert.Equal(["1", "2", "3"], attempts);
    }

    [SkippableFact]
    public async Task Permanent_failure_is_sent_to_dead_letter_queue()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions() with
        {
            ShouldRetryHandlerException = static _ => false
        };
        await using var probe = await DeadLetterProbe.CreateAsync(options);
        await using var transport = new RabbitMqMessagingProtocol(options);
        var calls = 0;

        await using var subscription = await transport.SubscribeAsync(
            "orders.permanent",
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new ArgumentException("sensitive handler details");
                }

                return ValueTask.CompletedTask;
            },
            new SubscriptionOptions { ConsumerGroup = $"workers-{Guid.NewGuid():N}" });

        await transport.SendAsync(
            "orders.permanent",
            new TransportEnvelope(new MessageHeaders(), "failed"u8.ToArray()));
        var deadLetter = await probe.GetAsync();

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal("orders.permanent", deadLetter.Headers[HeaderNames.DeadLetterSource]);
        Assert.Equal(typeof(ArgumentException).FullName, deadLetter.Headers[HeaderNames.DeadLetterErrorType]);
        Assert.Equal("Handler execution failed.", deadLetter.Headers[HeaderNames.DeadLetterError]);
        Assert.Equal("1", deadLetter.Headers[RabbitMqEnvelopeMapper.DeliveryAttemptHeader]);
    }

    [SkippableFact]
    public async Task Exhausted_transient_failure_is_sent_to_dead_letter_queue()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions() with { MaxDeliveryAttempts = 3 };
        await using var probe = await DeadLetterProbe.CreateAsync(options);
        await using var transport = new RabbitMqMessagingProtocol(options);
        var calls = 0;

        await using var subscription = await transport.SubscribeAsync(
            "orders.exhausted",
            (_, _) =>
            {
                if (Interlocked.Increment(ref calls) <= 3)
                {
                    throw new InvalidOperationException("transient");
                }

                return ValueTask.CompletedTask;
            },
            new SubscriptionOptions { ConsumerGroup = $"workers-{Guid.NewGuid():N}" });

        await transport.SendAsync(
            "orders.exhausted",
            new TransportEnvelope(new MessageHeaders(), "failed"u8.ToArray()));
        var deadLetter = await probe.GetAsync();

        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal("3", deadLetter.Headers[RabbitMqEnvelopeMapper.DeliveryAttemptHeader]);
    }

    [SkippableFact]
    public async Task Classifier_failure_is_sanitized_and_sent_to_dead_letter_queue()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions() with
        {
            ShouldRetryHandlerException = static _ =>
                throw new ApplicationException("classifier secret")
        };
        await using var probe = await DeadLetterProbe.CreateAsync(options);
        await using var transport = new RabbitMqMessagingProtocol(options);
        var calls = 0;
        await using var subscription = await transport.SubscribeAsync(
            "orders.classifier",
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("handler secret");
            },
            new SubscriptionOptions { ConsumerGroup = $"workers-{Guid.NewGuid():N}" });

        await transport.SendAsync(
            "orders.classifier",
            new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));
        var deadLetter = await probe.GetAsync();

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(typeof(ApplicationException).FullName, deadLetter.Headers[HeaderNames.DeadLetterErrorType]);
        Assert.Equal("Handler execution failed.", deadLetter.Headers[HeaderNames.DeadLetterError]);
        Assert.DoesNotContain("secret", deadLetter.Headers.Values);
    }

    private sealed class DeadLetterProbe(IConnection connection, IChannel channel, string queueName)
        : IAsyncDisposable
    {
        public static async ValueTask<DeadLetterProbe> CreateAsync(RabbitMqProtocolOptions options)
        {
            var connection = await new ConnectionFactory { Uri = options.ConnectionUri }
                .CreateConnectionAsync();
            var channel = await connection.CreateChannelAsync();
            var entityName = options.ExchangeName + options.DeadLetterSuffix;
            await channel.ExchangeDeclareAsync(entityName, ExchangeType.Topic, durable: true, autoDelete: false);
            await channel.QueueDeclareAsync(entityName, durable: true, exclusive: false, autoDelete: false);
            await channel.QueueBindAsync(entityName, entityName, "#");
            return new DeadLetterProbe(connection, channel, entityName);
        }

        public async ValueTask<TransportEnvelope> GetAsync()
        {
            using var timeout = new CancellationTokenSource(Timeout);
            while (!timeout.IsCancellationRequested)
            {
                var result = await channel.BasicGetAsync(queueName, autoAck: true, timeout.Token);
                if (result is not null)
                {
                    return RabbitMqEnvelopeMapper.FromDelivery(result.BasicProperties, result.Body);
                }

                await Task.Delay(50, timeout.Token);
            }

            throw new TimeoutException("No RabbitMQ dead-letter message was received.");
        }

        public async ValueTask DisposeAsync()
        {
            await channel.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
