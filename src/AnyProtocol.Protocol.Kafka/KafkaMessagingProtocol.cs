using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace AnyProtocol.Protocol.Kafka;

/// <summary>
/// Implements kafka messaging messaging transport operations.
/// </summary>
public sealed class KafkaMessagingProtocol :
    IMessagingProtocol,
    IDeadLetterTransport,
    ITransportReadiness
{
    private const string ReplyChannelPrefix = "_anyprotocol.reply.";
    private readonly KafkaProtocolOptions _options;
    private readonly IProducer<string, byte[]> _producer;
    private readonly IAdminClient _admin;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _topics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, KafkaSubscription> _subscriptions = new();
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the KafkaMessagingProtocol class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public KafkaMessagingProtocol(KafkaProtocolOptions options)
    {
        _options = Validate(options);

        var producerConfig = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            EnableIdempotence = true,
            Acks = Acks.All
        };
        ApplyOverrides(producerConfig, options.ProducerConfig);
        _producer = new ProducerBuilder<string, byte[]>(producerConfig).Build();

        _admin = new AdminClientBuilder(
                new AdminClientConfig
                {
                    BootstrapServers = options.BootstrapServers,
                    ClientId = $"{options.ClientId}-admin"
                })
            .Build();
    }

    /// <summary>
    /// Gets the optional transport capabilities supported by this protocol.
    /// </summary>
    /// <value>The capabilities.</value>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.PublishSubscribe |
        TransportCapabilities.CompetingConsumers |
        TransportCapabilities.NativeHeaders;

    /// <summary>
    /// Gets the delivery and ordering guarantees provided by this protocol.
    /// </summary>
    /// <value>The semantics.</value>
    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtLeastOnce,
        Ordering = TransportOrdering.PerPartition,
        Durability = TransportDurability.Durable,
        SupportsPublishSubscribe = true,
        SupportsCompetingConsumers = true,
        SupportsPartitioning = true,
        SupportsBackpressure = true,
        SupportsCancellation = true
    };

    /// <summary>
    /// Sends a transport envelope to the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var topic = GetTopic(channel);
        await EnsureTopicAsync(topic, IsReplyChannel(channel), cancellationToken).ConfigureAwait(false);
        await ProduceAsync(topic, envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the subscribe async.</returns>
    public async ValueTask<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        options ??= new SubscriptionOptions();
        if (options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrency,
                "MaxConcurrency must be greater than zero.");
        }

        var topic = GetTopic(channel);
        var replyTopic = IsReplyChannel(channel);
        await EnsureTopicAsync(topic, replyTopic, cancellationToken).ConfigureAwait(false);
        var group = string.IsNullOrWhiteSpace(options.ConsumerGroup)
            ? $"{_options.ClientId}.{SanitizeGroup(channel)}.{Guid.NewGuid():N}"
            : options.ConsumerGroup;
        var id = Guid.NewGuid();
        var subscription = new KafkaSubscription(
            this,
            topic,
            group,
            handler,
            options.MaxConcurrency,
            replyTopic);
        if (!_subscriptions.TryAdd(id, subscription))
        {
            throw new InvalidOperationException("Could not register the Kafka subscription.");
        }

        subscription.Disposed += () => _subscriptions.TryRemove(id, out _);
        try
        {
            await subscription.StartAsync(cancellationToken).ConfigureAwait(false);
            return subscription;
        }
        catch
        {
            _subscriptions.TryRemove(id, out _);
            await subscription.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends a failed message to the channel configured for dead-letter delivery.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask SendToDeadLetterAsync(
        string channel,
        TransportEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return SendToDeadLetterTopicAsync(
            GetTopic(channel),
            envelope,
            exception,
            cancellationToken);
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var subscription in _subscriptions.Values.ToArray())
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }

        _subscriptions.Clear();
        _producer.Flush(_options.ProducerFlushTimeout);
        _producer.Dispose();
        _admin.Dispose();
    }

    /// <summary>
    /// Checks whether the transport is ready to handle messages without mutating application state.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the check readiness async.</returns>
    public ValueTask<TransportReadinessResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ValueTask.FromResult(
                TransportReadinessResult.NotReady("The Kafka transport is disposed."));
        }

        try
        {
            var metadata = _admin.GetMetadata(TimeSpan.FromSeconds(2));
            return ValueTask.FromResult(
                metadata.Brokers.Count > 0
                    ? TransportReadinessResult.Ready("Kafka broker metadata is available.")
                    : TransportReadinessResult.NotReady("Kafka returned no broker metadata."));
        }
        catch (KafkaException)
        {
            return ValueTask.FromResult(
                TransportReadinessResult.NotReady("Kafka broker metadata is unavailable."));
        }
    }

    private async ValueTask SendToDeadLetterTopicAsync(
        string sourceTopic,
        TransportEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(exception);
        if (!_options.EnableDeadLetter)
        {
            throw new InvalidOperationException(
                $"Kafka handler for topic '{sourceTopic}' failed and dead-letter publishing is disabled.",
                exception);
        }

        var deadLetterTopic = sourceTopic + _options.DeadLetterSuffix;
        var headers = new MessageHeaders(envelope.Headers)
        {
            [HeaderNames.DeadLetterSource] = sourceTopic,
            [HeaderNames.DeadLetterErrorType] = exception.GetType().FullName ?? exception.GetType().Name,
            [HeaderNames.DeadLetterError] = "Handler execution failed."
        };
        await EnsureTopicAsync(deadLetterTopic, false, cancellationToken).ConfigureAwait(false);
        await ProduceAsync(
                deadLetterTopic,
                new TransportEnvelope(headers, envelope.Body),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProduceAsync(
        string topic,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var headers = new Headers();
        foreach (var header in envelope.Headers)
        {
            headers.Add(header.Key, Encoding.UTF8.GetBytes(header.Value));
        }

        await _producer.ProduceAsync(
                topic,
                new Message<string, byte[]>
                {
                    Key = envelope.Headers[HeaderNames.PartitionKey]!,
                    Value = envelope.Body.ToArray(),
                    Headers = headers
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureTopicAsync(
        string topic,
        bool replyTopic,
        CancellationToken cancellationToken)
    {
        if (!_options.AutoCreateTopics)
        {
            return;
        }

        var creation = _topics.GetOrAdd(
            topic,
            name => new Lazy<Task>(
                () => CreateTopicAsync(name, replyTopic),
                LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            await creation.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_topics.TryGetValue(topic, out var current) &&
                ReferenceEquals(current, creation))
            {
                _topics.TryRemove(topic, out _);
            }

            throw;
        }
    }

    private async Task CreateTopicAsync(string topic, bool replyTopic)
    {
        var specification = new TopicSpecification
        {
            Name = topic,
            NumPartitions = _options.TopicPartitions,
            ReplicationFactor = _options.TopicReplicationFactor
        };
        if (replyTopic)
        {
            specification.Configs = new Dictionary<string, string>
            {
                ["cleanup.policy"] = "delete",
                ["retention.ms"] = ((long)_options.ReplyTopicRetention.TotalMilliseconds)
                    .ToString(CultureInfo.InvariantCulture)
            };
        }

        try
        {
            await _admin.CreateTopicsAsync([specification]).ConfigureAwait(false);
        }
        catch (CreateTopicsException exception)
            when (exception.Results.All(result => result.Error.Code == ErrorCode.TopicAlreadyExists))
        {
        }
    }

    private ConsumerConfig CreateConsumerConfig(string group, bool replyTopic)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            ClientId = $"{_options.ClientId}-consumer",
            GroupId = group,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            AutoOffsetReset = replyTopic ? AutoOffsetReset.Latest : AutoOffsetReset.Earliest,
            AllowAutoCreateTopics = _options.AutoCreateTopics
        };
        ApplyOverrides(config, _options.ConsumerConfig);
        return config;
    }

    private string GetTopic(string channel)
    {
        var topic = string.IsNullOrWhiteSpace(_options.TopicPrefix)
            ? channel
            : $"{_options.TopicPrefix!.Trim('.')}.{channel}";
        if (topic.Length > 249 ||
            topic.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                $"Channel '{channel}' cannot be mapped to a valid Kafka topic.",
                nameof(channel));
        }

        return topic;
    }

    private static KafkaProtocolOptions Validate(KafkaProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        if (options.TopicPartitions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TopicPartitions));
        }

        if (options.TopicReplicationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TopicReplicationFactor));
        }

        if (options.ReplyTopicRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ReplyTopicRetention));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeadLetterSuffix);
        if (options.SubscriptionStartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SubscriptionStartupTimeout));
        }

        if (options.ConsumerPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ConsumerPollInterval));
        }

        if (options.ProducerFlushTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProducerFlushTimeout));
        }

        return options;
    }

    private static void ApplyOverrides(ClientConfig config, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var pair in overrides)
        {
            config.Set(pair.Key, pair.Value);
        }
    }

    private static bool IsReplyChannel(string channel)
        => channel.StartsWith(ReplyChannelPrefix, StringComparison.Ordinal);

    private static string SanitizeGroup(string value)
        => string.Concat(
            value.Select(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                    ? character
                    : '-'));

    private static TransportEnvelope CreateEnvelope(ConsumeResult<string, byte[]> result)
    {
        var headers = new MessageHeaders();
        foreach (var header in result.Message.Headers)
        {
            var value = header.GetValueBytes();
            if (value is not null)
            {
                headers[header.Key] = Encoding.UTF8.GetString(value);
            }
        }

        return new TransportEnvelope(headers, result.Message.Value ?? []);
    }

    private sealed class KafkaSubscription : IAsyncDisposable
    {
        private readonly KafkaMessagingProtocol _owner;
        private readonly string _topic;
        private readonly string _group;
        private readonly Func<TransportEnvelope, CancellationToken, ValueTask> _handler;
        private readonly int _workerCount;
        private readonly bool _replyTopic;
        private readonly CancellationTokenSource _stopping = new();
        private readonly TaskCompletionSource _assigned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task[] _workers = [];
        private int _disposed;

        public KafkaSubscription(
            KafkaMessagingProtocol owner,
            string topic,
            string group,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            int workerCount,
            bool replyTopic)
        {
            _owner = owner;
            _topic = topic;
            _group = group;
            _handler = handler;
            _workerCount = workerCount;
            _replyTopic = replyTopic;
        }

        public event Action? Disposed;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _workers = Enumerable.Range(0, _workerCount)
                .Select(index => Task.Run(() => ConsumeAsync(index), CancellationToken.None))
                .ToArray();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _stopping.Token);
            timeout.CancelAfter(_owner._options.SubscriptionStartupTimeout);
            try
            {
                await _assigned.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Kafka subscription for topic '{_topic}' was not assigned within " +
                    $"{_owner._options.SubscriptionStartupTimeout}.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopping.Cancel();
            try
            {
                await Task.WhenAll(_workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            finally
            {
                _stopping.Dispose();
                Disposed?.Invoke();
            }
        }

        private async Task ConsumeAsync(int workerIndex)
        {
            var builder = new ConsumerBuilder<string, byte[]>(
                _owner.CreateConsumerConfig(_group, _replyTopic));
            if (_replyTopic)
            {
                builder.SetPartitionsAssignedHandler(
                    (consumer, partitions) =>
                    {
                        var offsets = partitions
                            .Select(
                                partition =>
                                {
                                    var watermark = consumer.QueryWatermarkOffsets(
                                        partition,
                                        _owner._options.SubscriptionStartupTimeout);
                                    return new TopicPartitionOffset(partition, watermark.High);
                                })
                            .ToArray();
                        _assigned.TrySetResult();
                        return offsets;
                    });
            }
            else
            {
                builder.SetPartitionsAssignedHandler((_, _) => _assigned.TrySetResult());
            }

            using var consumer = builder.Build();
            consumer.Subscribe(_topic);
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    ConsumeResult<string, byte[]>? result;
                    try
                    {
                        result = consumer.Consume(_owner._options.ConsumerPollInterval);
                    }
                    catch (ConsumeException exception) when (exception.Error.IsFatal)
                    {
                        throw new InvalidOperationException(
                            $"Kafka consumer '{_group}/{workerIndex}' failed: {exception.Error.Reason}",
                            exception);
                    }

                    if (result is null || result.IsPartitionEOF)
                    {
                        continue;
                    }

                    var envelope = CreateEnvelope(result);
                    try
                    {
                        await _handler(envelope, _stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception exception)
                    {
                        try
                        {
                            await _owner.SendToDeadLetterTopicAsync(
                                    result.Topic,
                                    envelope,
                                    exception,
                                    _stopping.Token)
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                            _stopping.Cancel();
                            throw;
                        }
                    }

                    consumer.Commit(result);
                }
            }
            finally
            {
                consumer.Close();
            }
        }
    }
}
