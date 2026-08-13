using System.Collections.Concurrent;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Routes unary emulated replies to the operation that registered their correlation identifier.
/// </summary>
internal sealed class ReplyInbox : IAsyncDisposable
{
    private enum InboxState
    {
        Created,
        Starting,
        Started,
        Disposing,
        Disposed
    }

    private readonly IMessagingProtocol _transport;
    private readonly string _replyChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransportEnvelope>> _pending = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private ITransportSubscription? _subscription;
    private int _state;

    public ReplyInbox(IMessagingProtocol transport, string replyChannel)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _replyChannel = string.IsNullOrWhiteSpace(replyChannel)
            ? throw new ArgumentException("A reply channel is required.", nameof(replyChannel))
            : replyChannel;
    }

    public async ValueTask RegisterAsync(
        string correlationId,
        TaskCompletionSource<TransportEnvelope> completion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(completion);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == InboxState.Created)
            {
                Volatile.Write(ref _state, (int)InboxState.Starting);
                try
                {
                    var subscriptionTransport = _transport as ISubscriptionTransport ??
                        throw new InvalidOperationException(
                            "Request/reply emulation requires an ISubscriptionTransport implementation.");
                    _subscription = await subscriptionTransport.SubscribeAsync(
                            _replyChannel,
                            HandleReplyAsync,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _state, (int)InboxState.Started);
                }
                catch
                {
                    Volatile.Write(ref _state, (int)InboxState.Created);
                    throw;
                }
            }

            if (State != InboxState.Started)
            {
                ThrowIfDisposed();
                throw new InvalidOperationException($"Reply inbox is {State}.");
            }

            if (!_pending.TryAdd(correlationId, completion))
            {
                throw new InvalidOperationException(
                    $"A request with correlation id '{correlationId}' is already pending.");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void Unregister(string correlationId, TaskCompletionSource<TransportEnvelope> completion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(completion);

        ((ICollection<KeyValuePair<string, TaskCompletionSource<TransportEnvelope>>>)_pending)
            .Remove(new KeyValuePair<string, TaskCompletionSource<TransportEnvelope>>(
                correlationId,
                completion));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == InboxState.Disposed)
            {
                return;
            }

            Volatile.Write(ref _state, (int)InboxState.Disposing);
            var disposedException = new ObjectDisposedException(nameof(ReplyInbox));
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(disposedException);
            }

            _pending.Clear();
            if (_subscription is not null)
            {
                await _subscription.DisposeAsync().ConfigureAwait(false);
                _subscription = null;
            }

            Volatile.Write(ref _state, (int)InboxState.Disposed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private InboxState State => (InboxState)Volatile.Read(ref _state);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(
            State is InboxState.Disposing or InboxState.Disposed,
            this);

    private ValueTask HandleReplyAsync(TransportEnvelope envelope, CancellationToken cancellationToken)
    {
        var correlationId = envelope.Headers[HeaderNames.CorrelationId];
        if (correlationId is not null && _pending.TryGetValue(correlationId, out var completion))
        {
            completion.TrySetResult(envelope);
        }

        return ValueTask.CompletedTask;
    }
}
