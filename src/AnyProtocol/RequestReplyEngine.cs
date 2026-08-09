using System.Collections.Concurrent;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Provides the request reply engine implementation used by AnyProtocol applications.
/// </summary>
public sealed class RequestReplyEngine : IAsyncDisposable
{
    private enum EngineState
    {
        Created,
        Starting,
        Started,
        Disposing,
        Disposed
    }

    private readonly IMessagingProtocol _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TransportEnvelope>> _pending = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _replyChannel = $"_anyprotocol.reply.{Guid.NewGuid():N}";
    private IAsyncDisposable? _replySubscription;
    private int _state;

    /// <summary>
    /// Initializes a new instance of the RequestReplyEngine class.
    /// </summary>
    /// <param name="transport">The transport.</param>
    public RequestReplyEngine(IMessagingProtocol transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the request async.</returns>
    public ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
        => RequestAsync(channel, request, timeout, cancellationToken, null);

    /// <summary>
    /// Sends a request and waits for its response.
    /// </summary>
    /// <param name="channel">The request channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="method">The optional contract method metadata.</param>
    /// <returns>A task whose result contains the request async.</returns>
    public async ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        ContractMethodDescriptor? method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive or infinite.");
        }

        ThrowIfDisposed();
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            operationCancellation.CancelAfter(timeout);
        }

        if (_transport is IMethodAwareRequestReplyTransport methodAwareTransport && method is not null)
        {
            try
            {
                return await methodAwareTransport.RequestAsync(
                        channel,
                        request,
                        method,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(RequestReplyEngine));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No response was received from '{channel}' within {timeout}.");
            }
        }

        if (_transport is IRequestReplyTransport nativeTransport)
        {
            try
            {
                return await nativeTransport.RequestAsync(
                        channel,
                        request,
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(RequestReplyEngine));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"No response was received from '{channel}' within {timeout}.");
            }
        }

        var messageId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var headers = new MessageHeaders(request.Headers);
        headers.Set(HeaderNames.MessageId, messageId);
        headers.Set(HeaderNames.CorrelationId, messageId);
        headers.Set(HeaderNames.ReplyTo, _replyChannel);
        headers.Set(HeaderNames.Channel, channel);
        headers.Set(HeaderNames.MessageType, MessageType.Request.ToString());

        try
        {
            await StartAndRegisterAsync(messageId, completion, operationCancellation.Token)
                .ConfigureAwait(false);
            var sendTransport = _transport as ISendTransport ??
                throw new InvalidOperationException(
                    "Request/reply emulation requires an ISendTransport implementation.");
            await sendTransport.SendAsync(
                    channel,
                    new TransportEnvelope(headers, request.Body),
                    operationCancellation.Token)
                .ConfigureAwait(false);
            return await completion.Task.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(RequestReplyEngine));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response was received from '{channel}' within {timeout}.");
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    /// <summary>
    /// Publishes a message to all subscribers of its configured channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask PublishAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return (_transport as ISendTransport ??
                throw new InvalidOperationException(
                    "Publishing requires an ISendTransport implementation."))
            .SendAsync(channel, envelope, cancellationToken);
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == EngineState.Disposed)
            {
                return;
            }

            Volatile.Write(ref _state, (int)EngineState.Disposing);
            var disposedException = new ObjectDisposedException(nameof(RequestReplyEngine));
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(disposedException);
            }

            _pending.Clear();
            if (_replySubscription is not null)
            {
                await _replySubscription.DisposeAsync().ConfigureAwait(false);
                _replySubscription = null;
            }

            Volatile.Write(ref _state, (int)EngineState.Disposed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private EngineState State => (EngineState)Volatile.Read(ref _state);

    private async ValueTask StartAndRegisterAsync(
        string messageId,
        TaskCompletionSource<TransportEnvelope> completion,
        CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State == EngineState.Created)
            {
                Volatile.Write(ref _state, (int)EngineState.Starting);
                try
                {
                    var subscriptionTransport = _transport as ISubscriptionTransport ??
                        throw new InvalidOperationException(
                            "Request/reply emulation requires an ISubscriptionTransport implementation.");
                    _replySubscription = await subscriptionTransport.SubscribeAsync(
                            _replyChannel,
                            HandleReplyAsync,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _state, (int)EngineState.Started);
                }
                catch
                {
                    Volatile.Write(ref _state, (int)EngineState.Created);
                    throw;
                }
            }

            if (State != EngineState.Started)
            {
                ThrowIfDisposed();
                throw new InvalidOperationException($"Request/reply engine is {State}.");
            }

            if (!_pending.TryAdd(messageId, completion))
            {
                throw new InvalidOperationException($"A request with id '{messageId}' is already pending.");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(
            State is EngineState.Disposing or EngineState.Disposed,
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
