using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Services;

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
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _replyChannel = $"_anyprotocol.reply.{Guid.NewGuid():N}";
    private readonly ReplyInbox _replyInbox;
    private int _state;

    /// <summary>
    /// Initializes a new instance of the RequestReplyEngine class.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    public RequestReplyEngine(
        IMessagingProtocol transport,
        IMessageEnvelopeFactory? envelopeFactory = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
        _replyInbox = new ReplyInbox(_transport, _replyChannel);
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

        var headers = _envelopeFactory.CreateOutboundHeaders(
            MessageType.Request,
            channel,
            request.Headers[HeaderNames.Contract],
            request.Headers[HeaderNames.Method],
            request.Headers[HeaderNames.ContentType],
            request.Headers);
        var messageId = headers[HeaderNames.MessageId]!;
        if (string.IsNullOrWhiteSpace(headers[HeaderNames.CorrelationId]))
        {
            headers[HeaderNames.CorrelationId] = messageId;
        }
        var correlationId = headers[HeaderNames.CorrelationId]!;
        var outboundRequest = new TransportEnvelope(headers, request.Body);

        if (_transport is IMethodAwareRequestReplyTransport methodAwareTransport && method is not null)
        {
            try
            {
                    return await methodAwareTransport.RequestAsync(
                        channel,
                        outboundRequest,
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
                        outboundRequest,
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

        var completion = new TaskCompletionSource<TransportEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        headers.Set(HeaderNames.ReplyTo, _replyChannel);

        try
        {
            await _replyInbox.RegisterAsync(correlationId, completion, operationCancellation.Token)
                .ConfigureAwait(false);
            var sendTransport = _transport as ISendTransport ??
                throw new InvalidOperationException(
                    "Request/reply emulation requires an ISendTransport implementation.");
            await sendTransport.SendAsync(
                    channel,
                    outboundRequest,
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
            _replyInbox.Unregister(correlationId, completion);
        }
    }

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (State == EngineState.Disposed)
        {
            return;
        }

        Volatile.Write(ref _state, (int)EngineState.Disposing);
        await _replyInbox.DisposeAsync().ConfigureAwait(false);

        Volatile.Write(ref _state, (int)EngineState.Disposed);
    }

    private EngineState State => (EngineState)Volatile.Read(ref _state);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(
            State is EngineState.Disposing or EngineState.Disposed,
            this);
}
