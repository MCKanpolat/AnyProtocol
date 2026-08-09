using System.Collections;
using System.Threading.Channels;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.InMemory;

/// <summary>
/// Implements in memory messaging messaging transport operations.
/// </summary>
public sealed class InMemoryMessagingProtocol : ISendTransport, ISubscriptionTransport, ITransportReadiness
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Subscription>> _subscriptions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _groupCounters =
        new(StringComparer.Ordinal);
    private readonly InMemoryProtocolOptions _options;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the InMemoryMessagingProtocol class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public InMemoryMessagingProtocol(InMemoryProtocolOptions? options = null)
    {
        _options = options ?? new InMemoryProtocolOptions();
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
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.PerChannel,
        Durability = TransportDurability.Volatile,
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
    public async ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(envelope);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var injectedFault = _options.FaultInjector?.Invoke(channel, envelope);
        if (injectedFault is not null)
        {
            throw injectedFault;
        }

        if (_options.DeliveryDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.DeliveryDelay, cancellationToken).ConfigureAwait(false);
        }

        Subscription[] targets;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(channel, out var subscribers))
            {
                return;
            }

            var selected = subscribers
                .Where(subscription => subscription.ConsumerGroup is null)
                .ToList();

            foreach (var group in subscribers
                         .Where(subscription => subscription.ConsumerGroup is not null)
                         .GroupBy(subscription => subscription.ConsumerGroup!, StringComparer.Ordinal))
            {
                var counterKey = $"{channel}\0{group.Key}";
                _groupCounters.TryGetValue(counterKey, out var counter);
                var groupMembers = group.ToArray();
                selected.Add(groupMembers[(int)((ulong)counter % (uint)groupMembers.Length)]);
                _groupCounters[counterKey] = counter + 1;
            }

            targets = selected.ToArray();
        }

        foreach (var target in targets)
        {
            await target.EnqueueAsync(CloneEnvelope(envelope), cancellationToken).ConfigureAwait(false);
        }
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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= new SubscriptionOptions();
        if (options.MaxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrency,
                "MaxConcurrency must be greater than zero.");
        }

        var subscription = new Subscription(
            channel,
            options.ConsumerGroup,
            options.MaxConcurrency,
            handler,
            RemoveSubscription);

        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(channel, out var subscribers))
            {
                subscribers = [];
                _subscriptions[channel] = subscribers;
            }

            subscribers.Add(subscription);
        }

        return ValueTask.FromResult<IAsyncDisposable>(subscription);
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.Values.SelectMany(value => value).ToArray();
            _subscriptions.Clear();
            _groupCounters.Clear();
        }

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
        return ValueTask.FromResult(
            _disposed
                ? TransportReadinessResult.NotReady("The in-memory transport is disposed.")
                : TransportReadinessResult.Ready("The in-memory transport is available."));
    }

    private void RemoveSubscription(Subscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscription.Channel, out var subscribers))
            {
                return;
            }

            subscribers.Remove(subscription);
            if (subscribers.Count == 0)
            {
                _subscriptions.Remove(subscription.Channel);
            }
        }
    }

    private static TransportEnvelope CloneEnvelope(TransportEnvelope envelope)
        => new(new SnapshotHeaders(envelope.Headers), envelope.Body.ToArray());

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly Channel<TransportEnvelope> _queue;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task[] _workers;
        private readonly Func<TransportEnvelope, CancellationToken, ValueTask> _handler;
        private readonly Action<Subscription> _remove;
        private int _disposed;

        public Subscription(
            string channel,
            string? consumerGroup,
            int maxConcurrency,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            Action<Subscription> remove)
        {
            Channel = channel;
            ConsumerGroup = consumerGroup;
            _handler = handler;
            _remove = remove;
            _queue = System.Threading.Channels.Channel.CreateUnbounded<TransportEnvelope>(
                new UnboundedChannelOptions
                {
                    SingleReader = maxConcurrency == 1,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            _workers = Enumerable.Range(0, maxConcurrency)
                .Select(_ => Task.Run(ConsumeAsync))
                .ToArray();
        }

        public string Channel { get; }

        public string? ConsumerGroup { get; }

        public ValueTask EnqueueAsync(
            TransportEnvelope envelope,
            CancellationToken cancellationToken)
            => _queue.Writer.WriteAsync(envelope, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _remove(this);
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
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var envelope in _queue.Reader.ReadAllAsync(_stopping.Token)
                                   .ConfigureAwait(false))
                {
                    await _handler(envelope, _stopping.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _queue.Writer.TryComplete(exception);
                _stopping.Cancel();
                throw;
            }
        }
    }

    private sealed class SnapshotHeaders : IMessageHeaders
    {
        private readonly Dictionary<string, string> _values;

        public SnapshotHeaders(IEnumerable<KeyValuePair<string, string>> values)
        {
            _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }

        public string? this[string key]
        {
            get => _values.GetValueOrDefault(key);
            set
            {
                if (value is null)
                {
                    _values.Remove(key);
                }
                else
                {
                    _values[key] = value;
                }
            }
        }

        string IReadOnlyDictionary<string, string>.this[string key] => _values[key];

        public IEnumerable<string> Keys => _values.Keys;

        public IEnumerable<string> Values => _values.Values;

        public int Count => _values.Count;

        public void Set(string key, string value) => _values[key] = value;

        public bool Remove(string key) => _values.Remove(key);

        public bool ContainsKey(string key) => _values.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
