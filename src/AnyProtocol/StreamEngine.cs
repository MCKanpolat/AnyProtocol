using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Services;

namespace AnyProtocol;

/// <summary>
/// Provides the stream engine implementation used by AnyProtocol applications.
/// </summary>
public sealed class StreamEngine : IAsyncDisposable
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
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly int _capacity;
    private readonly ConcurrentDictionary<string, Channel<TransportEnvelope>> _pending = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _replyChannel = $"_anyprotocol.stream.{Guid.NewGuid():N}";
    private ITransportSubscription? _replySubscription;
    private int _state;

    /// <summary>
    /// Initializes a new instance of the StreamEngine class.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <param name="capacity">The capacity.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    public StreamEngine(
        IMessagingProtocol transport,
        int capacity = 32,
        IMessageEnvelopeFactory? envelopeFactory = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
    }

    /// <summary>
    /// Performs the stream async operation.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="timeout">The maximum time allowed for the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    public async IAsyncEnumerable<TransportEnvelope> StreamAsync(
        string channel,
        TransportEnvelope request,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(request);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        ThrowIfDisposed();
        var headers = _envelopeFactory.CreateOutboundHeaders(
            MessageType.Request,
            channel,
            request.Headers[HeaderNames.Contract],
            request.Headers[HeaderNames.Method],
            request.Headers[HeaderNames.ContentType],
            request.Headers,
            replyTo: _replyChannel);
        var messageId = headers[HeaderNames.MessageId]!;
        if (string.IsNullOrWhiteSpace(headers[HeaderNames.CorrelationId]))
        {
            headers[HeaderNames.CorrelationId] = messageId;
        }
        var responses = Channel.CreateBounded<TransportEnvelope>(
            new BoundedChannelOptions(_capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        using var operationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            operationCancellation.CancelAfter(timeout);
        }

        try
        {
            await StartAndRegisterAsync(messageId, responses, operationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(StreamEngine));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Stream '{channel}' did not start within {timeout}.");
        }

        try
        {
            try
            {
                var sendTransport = _transport as ISendTransport ??
                    throw new InvalidOperationException(
                        "Stream emulation requires an ISendTransport implementation.");
                await sendTransport.SendAsync(
                        channel,
                        new TransportEnvelope(headers, request.Body),
                        operationCancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(StreamEngine));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Stream '{channel}' did not complete within {timeout}.");
            }

            long expectedSequence = 0;
            await using var enumerator = responses.Reader
                .ReadAllAsync(operationCancellation.Token)
                .GetAsyncEnumerator(operationCancellation.Token);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(StreamEngine));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"Stream '{channel}' did not complete within {timeout}.");
                }
                catch (ChannelClosedException) when (_lifetime.IsCancellationRequested)
                {
                    throw new ObjectDisposedException(nameof(StreamEngine));
                }

                if (!hasNext)
                {
                    break;
                }

                var response = enumerator.Current;
                var messageType = response.Headers.Get(
                    HeaderNames.MessageType,
                    MessageType.StreamItem);
                if (messageType == MessageType.StreamComplete)
                {
                    yield break;
                }

                if (messageType == MessageType.StreamItem)
                {
                    var rawSequence = response.Headers[HeaderNames.StreamSequence];
                    if (!long.TryParse(rawSequence, out var sequence) || sequence != expectedSequence)
                    {
                        throw new InvalidDataException(
                            $"Stream '{messageId}' expected sequence {expectedSequence}, " +
                            $"but received '{rawSequence ?? "<missing>"}'.");
                    }

                    expectedSequence++;
                }

                yield return response;
                if (messageType == MessageType.Fault)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (_pending.TryRemove(messageId, out var pending))
            {
                pending.Writer.TryComplete();
            }
        }
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
            var disposedException = new ObjectDisposedException(nameof(StreamEngine));
            foreach (var stream in _pending.Values)
            {
                stream.Writer.TryComplete(disposedException);
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
        Channel<TransportEnvelope> responses,
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
                            "Stream emulation requires an ISubscriptionTransport implementation.");
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
                throw new InvalidOperationException($"Stream engine is {State}.");
            }

            if (!_pending.TryAdd(messageId, responses))
            {
                throw new InvalidOperationException($"A stream with id '{messageId}' is already pending.");
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

    private async ValueTask HandleReplyAsync(
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var correlationId = envelope.Headers[HeaderNames.CorrelationId];
        if (correlationId is not null && _pending.TryGetValue(correlationId, out var stream))
        {
            await stream.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
    }
}
