using System.Collections.Concurrent;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using RabbitMQ.Client;

namespace AnyProtocol.Protocol.RabbitMq;

/// <summary>
/// Implements rabbit mq messaging messaging transport operations.
/// </summary>
public sealed class RabbitMqMessagingProtocol :
    ISendTransport,
    ISubscriptionTransport,
    IDeadLetterTransport,
    ITransportReadiness
{
    private readonly RabbitMqProtocolOptions _options;
    private readonly RabbitMqTopology _topology;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RabbitMqSubscription> _subscriptions = new();
    private IConnection? _connection;
    private IChannel? _publishChannel;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the RabbitMqMessagingProtocol class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public RabbitMqMessagingProtocol(RabbitMqProtocolOptions options)
    {
        _options = Validate(options);
        _topology = new RabbitMqTopology(_options);
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
        Ordering = TransportOrdering.PerChannel,
        Durability = TransportDurability.Durable,
        SupportsPublishSubscribe = true,
        SupportsCompetingConsumers = true,
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
    public ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
        => PublishAsync(channel, envelope, cancellationToken);

    /// <summary>
    /// Sends a failed message to the channel configured for dead-letter delivery.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask SendToDeadLetterAsync(
        string channel,
        TransportEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(exception);
        if (!_options.EnableDeadLetter)
        {
            throw new InvalidOperationException(
                $"RabbitMQ handler for channel '{channel}' failed and dead-letter publishing is disabled.",
                exception);
        }

        var headers = new MessageHeaders(envelope.Headers)
        {
            [HeaderNames.DeadLetterSource] = channel,
            [HeaderNames.DeadLetterErrorType] = exception.GetType().FullName ?? exception.GetType().Name,
            [HeaderNames.DeadLetterError] = "Handler execution failed."
        };
        var attempt = GetDeliveryAttempt(envelope);
        await PublishConfirmedAsync(
            _topology.DeadLetterExchange,
            _topology.RoutingKey(channel),
            new TransportEnvelope(headers, envelope.Body),
            attempt,
            declareDeadLetterTopology: true,
            cancellationToken);
    }

    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the subscribe async.</returns>
    public ValueTask<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
        => SubscribeCoreAsync(channel, handler, options, cancellationToken);

    /// <summary>
    /// Checks whether the transport is ready to handle messages without mutating application state.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the check readiness async.</returns>
    public async ValueTask<TransportReadinessResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return TransportReadinessResult.NotReady("The RabbitMQ transport is disposed.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ReadinessTimeout);
        try
        {
            var channel = await EnsureConnectedAsync(timeout.Token);
            await channel.ExchangeDeclarePassiveAsync(_options.ExchangeName, timeout.Token);
            return channel.IsOpen && _connection?.IsOpen == true
                ? TransportReadinessResult.Ready("RabbitMQ connection and exchange are available.")
                : TransportReadinessResult.NotReady("RabbitMQ connection is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return TransportReadinessResult.NotReady("RabbitMQ broker is unavailable.");
        }
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

        using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
        foreach (var subscription in _subscriptions.Values.ToArray())
        {
            try
            {
                await subscription.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        _subscriptions.Clear();
        await _connectionGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_publishChannel is not null)
            {
                await _publishChannel.CloseAsync();
                await _publishChannel.DisposeAsync();
                _publishChannel = null;
            }

            if (_connection is not null)
            {
                await _connection.CloseAsync(_options.ShutdownTimeout);
                await _connection.DisposeAsync();
                _connection = null;
            }
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
            _publishGate.Dispose();
        }
    }

    private async ValueTask PublishAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(envelope);
        await PublishConfirmedAsync(
            _options.ExchangeName,
            _topology.RoutingKey(channel),
            envelope,
            deliveryAttempt: 1,
            declareDeadLetterTopology: false,
            cancellationToken);
    }

    internal ValueTask RepublishAsync(
        string channel,
        TransportEnvelope envelope,
        int deliveryAttempt,
        CancellationToken cancellationToken)
        => PublishConfirmedAsync(
            _options.ExchangeName,
            _topology.RoutingKey(channel),
            envelope,
            deliveryAttempt,
            declareDeadLetterTopology: false,
            cancellationToken);

    private async ValueTask PublishConfirmedAsync(
        string exchange,
        string routingKey,
        TransportEnvelope envelope,
        int deliveryAttempt,
        bool declareDeadLetterTopology,
        CancellationToken cancellationToken)
    {
        var publishChannel = await EnsureConnectedAsync(cancellationToken);
        var properties = RabbitMqEnvelopeMapper.ToProperties(envelope, deliveryAttempt);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ConfirmTimeout);

        await _publishGate.WaitAsync(timeout.Token);
        try
        {
            if (declareDeadLetterTopology)
            {
                await DeclareDeadLetterTopologyAsync(publishChannel, timeout.Token);
            }

            await publishChannel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: true,
                properties,
                envelope.Body,
                timeout.Token);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException(
                "RabbitMQ publisher confirmation timed out.");
        }
        finally
        {
            _publishGate.Release();
        }
    }

    private async ValueTask DeclareDeadLetterTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            _topology.DeadLetterExchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            _topology.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _topology.DeadLetterQueue,
            _topology.DeadLetterExchange,
            "#",
            cancellationToken: cancellationToken);
    }

    private async ValueTask<IAsyncDisposable> SubscribeCoreAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new SubscriptionOptions();
        if (options.MaxConcurrency is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxConcurrency));
        }

        await EnsureConnectedAsync(cancellationToken);
        var connection = _connection ??
            throw new InvalidOperationException("RabbitMQ connection was not initialized.");
        var subscription = await RabbitMqSubscription.CreateAsync(
            this,
            connection,
            _options,
            _topology.ForSubscription(channel, options.ConsumerGroup),
            channel,
            handler,
            options.MaxConcurrency,
            cancellationToken);
        var id = Guid.NewGuid();
        if (!_subscriptions.TryAdd(id, subscription))
        {
            await subscription.DisposeAsync();
            throw new InvalidOperationException("Could not register the RabbitMQ subscription.");
        }

        subscription.Disposed += () => _subscriptions.TryRemove(id, out _);
        return subscription;
    }

    internal int GetDeliveryAttempt(TransportEnvelope envelope)
        => int.TryParse(
                envelope.Headers[RabbitMqEnvelopeMapper.DeliveryAttemptHeader],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var attempt) && attempt > 0
            ? attempt
            : 1;

    internal bool ShouldRetry(Exception exception)
        => _options.ShouldRetryHandlerException(exception);

    internal int MaxDeliveryAttempts => _options.MaxDeliveryAttempts;

    internal bool DeadLetterEnabled => _options.EnableDeadLetter;

    private async ValueTask<IChannel> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection?.IsOpen == true && _publishChannel?.IsOpen == true)
        {
            return _publishChannel;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            if (_connection?.IsOpen == true && _publishChannel?.IsOpen == true)
            {
                return _publishChannel;
            }

            if (_publishChannel is not null)
            {
                await _publishChannel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }

            var factory = new ConnectionFactory
            {
                Uri = _options.ConnectionUri,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = _options.NetworkRecoveryInterval
            };
            _connection = await factory.CreateConnectionAsync(
                _options.ClientProvidedName,
                cancellationToken);
            _publishChannel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await _publishChannel.ExchangeDeclareAsync(
                _options.ExchangeName,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
            return _publishChannel;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static RabbitMqProtocolOptions Validate(RabbitMqProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ConnectionUri);
        if (!options.ConnectionUri.IsAbsoluteUri ||
            options.ConnectionUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException(
                "RabbitMQ connection URI must be an absolute amqp or amqps URI.",
                nameof(options));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientProvidedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExchangeName);
        if (options.PrefetchCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PrefetchCount));
        }

        if (options.MaxDeliveryAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxDeliveryAttempts));
        }

        ArgumentNullException.ThrowIfNull(options.ShouldRetryHandlerException);
        if (options.EnableDeadLetter)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.DeadLetterSuffix);
        }

        ValidatePositive(options.ConfirmTimeout, nameof(options.ConfirmTimeout));
        ValidatePositive(options.ReadinessTimeout, nameof(options.ReadinessTimeout));
        ValidatePositive(options.NetworkRecoveryInterval, nameof(options.NetworkRecoveryInterval));
        ValidatePositive(options.ShutdownTimeout, nameof(options.ShutdownTimeout));
        return options;
    }

    private static void ValidatePositive(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
