using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Logging.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using NetMQ;
using NetMQ.Sockets;

namespace AnyProtocol.Protocol.ZeroMq;

/// <summary>
/// Implements zero mq messaging messaging transport operations.
/// </summary>
public sealed class ZeroMqMessagingProtocol : ISendTransport, ISubscriptionTransport, ITransportReadiness
{
    private readonly IEnvelopeCodec _codec;
    private readonly ILogWriter _logger;
    private readonly ZeroMqProtocolOptions _options;
    private readonly Channel<OutgoingMessage> _outbound;
    private readonly ConcurrentDictionary<string, SubscriptionSet> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _socketLoop;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the ZeroMqMessagingProtocol class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public ZeroMqMessagingProtocol(ZeroMqProtocolOptions options)
        : this(options, new BinaryEnvelopeCodec(), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the ZeroMqMessagingProtocol class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="codec">The envelope codec.</param>
    /// <param name="logWriterFactory">The logging writer factory.</param>
    public ZeroMqMessagingProtocol(
        ZeroMqProtocolOptions options,
        IEnvelopeCodec codec,
        ILogWriterFactory? logWriterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(codec);
        options.Validate();
        _options = options;
        _codec = codec;
        _logger = (logWriterFactory ?? NullLogWriterFactory.Instance)
            .CreateLogWriter(nameof(ZeroMqMessagingProtocol));
        _outbound = Channel.CreateBounded<OutgoingMessage>(
            new BoundedChannelOptions(options.HighWatermark)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        _socketLoop = Task.Factory.StartNew(
                RunSocketLoop,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
        _ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the optional transport capabilities supported by this protocol.
    /// </summary>
    /// <value>The capabilities.</value>
    public TransportCapabilities Capabilities => TransportCapabilities.CompetingConsumers;

    /// <summary>
    /// Gets the delivery and ordering guarantees provided by this protocol.
    /// </summary>
    /// <value>The semantics.</value>
    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.PerChannel,
        Durability = TransportDurability.Volatile,
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
    public async ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new OutgoingMessage(
            channel,
            _codec.Encode(envelope).ToArray(),
            completion);
        await _outbound.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the subscribe async.</returns>
    public ValueTask<ITransportSubscription> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new SubscriptionOptions();
        if (options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrency,
                "MaxConcurrency must be greater than zero.");
        }

        var set = _subscriptions.GetOrAdd(channel, static _ => new SubscriptionSet());
        var subscription = new Subscription(
            channel,
            options.ConsumerGroup,
            options.MaxConcurrency,
            handler,
            RemoveSubscription,
            _logger,
            _options.SubscriptionQueueCapacity);
        set.Add(subscription);
        return ValueTask.FromResult<ITransportSubscription>(subscription);
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

        _outbound.Writer.TryComplete();
        _stopping.Cancel();
        await _socketLoop.ConfigureAwait(false);

        var subscriptions = _subscriptions.Values
            .SelectMany(static set => set.RemoveAll())
            .ToArray();
        _subscriptions.Clear();
        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
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
        if (Volatile.Read(ref _disposed) != 0 || _socketLoop.IsCompleted)
        {
            return ValueTask.FromResult(
                TransportReadinessResult.NotReady("The ZeroMQ socket loop is not running."));
        }

        return ValueTask.FromResult(
            _options.Role == ZeroMqRole.Server
                ? TransportReadinessResult.Ready("The ZeroMQ server sockets are bound.")
                : TransportReadinessResult.Unknown(
                    "ZeroMQ does not provide a non-destructive peer readiness signal."));
    }

    private Task RunSocketLoop()
    {
        try
        {
            if (_options.Role == ZeroMqRole.Server)
            {
                using var inbound = new RouterSocket();
                using var outbound = new PublisherSocket();
                Configure(inbound.Options);
                Configure(outbound.Options);
                inbound.Bind(_options.RouterEndpoint);
                outbound.Bind(_options.PublisherEndpoint);
                _ready.TrySetResult();
                PollServer(inbound, outbound);
            }
            else
            {
                using var outbound = new DealerSocket();
                using var inbound = new SubscriberSocket();
                Configure(outbound.Options);
                Configure(inbound.Options);
                outbound.Options.Identity = Encoding.UTF8.GetBytes(
                    _options.ClientIdentity ?? Guid.NewGuid().ToString("N"));
                outbound.Connect(_options.RouterEndpoint);
                inbound.Connect(_options.PublisherEndpoint);
                inbound.SubscribeToAnyTopic();
                _ready.TrySetResult();
                PollClient(outbound, inbound);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AnyProtocolDiagnostics.RecordTransportFailure("zeromq", "socket_loop");
            _ready.TrySetException(exception);
            FailPending(exception);
            _logger.LogEvent(
                AnyProtocolLogEvents.ZeroMqSocketLoopFailed,
                LogSeverity.Error,
                "The ZeroMQ socket loop failed during {0}; exception type '{1}'.",
                exception,
                _options.Role,
                exception.GetType().FullName);
            if (!_stopping.IsCancellationRequested)
            {
                throw;
            }
        }
        finally
        {
            FailPending(new ObjectDisposedException(nameof(ZeroMqMessagingProtocol)));
        }

        return Task.CompletedTask;
    }

    private void PollServer(RouterSocket inbound, PublisherSocket outbound)
    {
        while (!_stopping.IsCancellationRequested)
        {
            DrainOutbound(
                static (socket, message) => socket.SendMoreFrame(message.Channel)
                    .SendFrame(message.Frame),
                outbound);

            var message = new NetMQMessage();
            if (inbound.TryReceiveMultipartMessage(_options.PollInterval, ref message))
            {
                if (message.FrameCount == 3)
                {
                    Dispatch(message[1].ConvertToString(), message[2].ToByteArray());
                }
            }

            Thread.Yield();
        }
    }

    private void PollClient(DealerSocket outbound, SubscriberSocket inbound)
    {
        while (!_stopping.IsCancellationRequested)
        {
            DrainOutbound(
                static (socket, message) => socket.SendMoreFrame(message.Channel)
                    .SendFrame(message.Frame),
                outbound);

            var message = new NetMQMessage();
            if (inbound.TryReceiveMultipartMessage(_options.PollInterval, ref message))
            {
                if (message.FrameCount == 2)
                {
                    Dispatch(message[0].ConvertToString(), message[1].ToByteArray());
                }
            }

            Thread.Yield();
        }
    }

    private void DrainOutbound<TSocket>(
        Action<TSocket, OutgoingMessage> sender,
        TSocket socket)
    {
        while (_outbound.Reader.TryRead(out var message))
        {
            try
            {
                sender(socket, message);
                message.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                message.Completion.TrySetException(exception);
            }
        }
    }

    private void Dispatch(string channel, byte[] frame)
    {
        try
        {
            if (_subscriptions.TryGetValue(channel, out var subscriptions))
            {
                subscriptions.Dispatch(_codec.Decode(frame), _stopping.Token);
            }
        }
        catch (Exception exception)
        {
            AnyProtocolDiagnostics.RecordTransportFailure("zeromq", "decode");
            _logger.LogEvent(
                AnyProtocolLogEvents.ZeroMqDecodeFailed,
                LogSeverity.Error,
                "Failed to decode a ZeroMQ message; exception type '{0}'.",
                exception,
                exception.GetType().FullName);
        }
    }

    private void Configure(SocketOptions options)
    {
        options.SendHighWatermark = _options.HighWatermark;
        options.ReceiveHighWatermark = _options.HighWatermark;
        options.Linger = TimeSpan.Zero;
    }

    private void RemoveSubscription(Subscription subscription)
    {
        if (_subscriptions.TryGetValue(subscription.Channel, out var set) &&
            set.Remove(subscription))
        {
            _subscriptions.TryRemove(
                new KeyValuePair<string, SubscriptionSet>(subscription.Channel, set));
        }
    }

    private void FailPending(Exception exception)
    {
        var failed = 0;
        while (_outbound.Reader.TryRead(out var message))
        {
            failed++;
            message.Completion.TrySetException(exception);
        }

        if (failed != 0)
        {
            AnyProtocolDiagnostics.RecordTransportFailure("zeromq", "pending_outbound");
            _logger.LogEvent(
                AnyProtocolLogEvents.ZeroMqPendingOutboundFailed,
                LogSeverity.Error,
                "Failed {0} pending ZeroMQ outbound messages; exception type '{1}'.",
                exception,
                failed,
                exception.GetType().FullName);
        }
    }

    private sealed record OutgoingMessage(
        string Channel,
        byte[] Frame,
        TaskCompletionSource Completion);

    private sealed class SubscriptionSet
    {
        private readonly Lock _gate = new();
        private readonly List<Subscription> _items = [];
        private readonly Dictionary<string, long> _groupCounters = new(StringComparer.Ordinal);

        public void Add(Subscription subscription)
        {
            lock (_gate)
            {
                _items.Add(subscription);
            }
        }

        public bool Remove(Subscription subscription)
        {
            lock (_gate)
            {
                _items.Remove(subscription);
                return _items.Count == 0;
            }
        }

        public Subscription[] RemoveAll()
        {
            lock (_gate)
            {
                var result = _items.ToArray();
                _items.Clear();
                _groupCounters.Clear();
                return result;
            }
        }

        public void Dispatch(TransportEnvelope envelope, CancellationToken cancellationToken)
        {
            Subscription[] selected;
            lock (_gate)
            {
                var targets = _items
                    .Where(static item => item.ConsumerGroup is null)
                    .ToList();
                foreach (var group in _items
                             .Where(static item => item.ConsumerGroup is not null)
                             .GroupBy(static item => item.ConsumerGroup!, StringComparer.Ordinal))
                {
                    _groupCounters.TryGetValue(group.Key, out var counter);
                    var members = group.ToArray();
                    targets.Add(members[(int)((ulong)counter % (uint)members.Length)]);
                    _groupCounters[group.Key] = counter + 1;
                }

                selected = targets.ToArray();
            }

            foreach (var subscription in selected)
            {
                subscription.Enqueue(
                    new TransportEnvelope(
                        new MessageHeaders(envelope.Headers),
                        envelope.Body.ToArray()),
                    cancellationToken);
            }
        }
    }

    private sealed class Subscription : ITransportSubscription
    {
        private readonly Channel<TransportEnvelope> _queue;
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _queueWrites = new();
        private readonly Task[] _workers;
        private readonly Func<TransportEnvelope, CancellationToken, ValueTask> _handler;
        private readonly Action<Subscription> _remove;
        private readonly ILogWriter _logger;
        private readonly object _acceptingGate = new();
        private bool _accepting = true;
        private int _pendingAdmissions;
        private TaskCompletionSource? _admissionsDrained;
        private int _disposed;

        public Subscription(
            string channel,
            string? consumerGroup,
            int maxConcurrency,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            Action<Subscription> remove,
            ILogWriter logger,
            int queueCapacity)
        {
            Channel = channel;
            ConsumerGroup = consumerGroup;
            _handler = handler;
            _remove = remove;
            _logger = logger;
            _queue = System.Threading.Channels.Channel.CreateBounded<TransportEnvelope>(
                new BoundedChannelOptions(queueCapacity)
                {
                    SingleReader = maxConcurrency == 1,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false
                });
            _workers = Enumerable.Range(0, maxConcurrency)
                .Select(_ => Task.Run(ConsumeAsync))
                .ToArray();
        }

        public string Channel { get; }

        public string? ConsumerGroup { get; }

        public void Enqueue(TransportEnvelope envelope, CancellationToken cancellationToken)
        {
            lock (_acceptingGate)
            {
                if (!_accepting)
                {
                    return;
                }
            }

            if (_queue.Writer.TryWrite(envelope))
            {
                return;
            }

            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _queueWrites.Token);
            try
            {
                _queue.Writer.WriteAsync(envelope, linkedCancellation.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_queueWrites.IsCancellationRequested)
            {
            }
        }

        public async ValueTask StopAcceptingAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? pendingAdmissions = null;
            lock (_acceptingGate)
            {
                if (!_accepting)
                {
                    return;
                }

                _accepting = false;
                if (_pendingAdmissions != 0)
                {
                    _admissionsDrained ??= new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    pendingAdmissions = _admissionsDrained.Task;
                }
            }

            _queueWrites.Cancel();
            _remove(this);
            if (pendingAdmissions is not null)
            {
                await pendingAdmissions.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _ = StopAcceptingAsync();
            _queue.Writer.TryComplete();
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
                _queueWrites.Dispose();
            }
        }

        private async Task ConsumeAsync()
        {
            await foreach (var envelope in _queue.Reader.ReadAllAsync(_stopping.Token)
                               .ConfigureAwait(false))
            {
                try
                {
                    if (!TryReserveAdmission())
                    {
                        break;
                    }

                    ValueTask handling;
                    try
                    {
                        handling = _handler(envelope, _stopping.Token);
                    }
                    finally
                    {
                        CompleteAdmission();
                    }

                    await handling.ConfigureAwait(false);
                }
                catch (MessageAdmissionRejectedException)
                {
                    break;
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    AnyProtocolDiagnostics.RecordTransportFailure("zeromq", "handler");
                    _logger.LogEvent(
                        AnyProtocolLogEvents.ZeroMqHandlerFailed,
                        LogSeverity.Error,
                        "A ZeroMQ subscription handler failed; exception type '{0}'.",
                        exception,
                        exception.GetType().FullName);
                }
            }
        }

        private bool TryReserveAdmission()
        {
            lock (_acceptingGate)
            {
                if (!_accepting)
                {
                    return false;
                }

                _pendingAdmissions++;
                return true;
            }
        }

        private void CompleteAdmission()
        {
            lock (_acceptingGate)
            {
                _pendingAdmissions--;
                if (!_accepting && _pendingAdmissions == 0)
                {
                    _admissionsDrained?.TrySetResult();
                }
            }
        }
    }
}
