using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using Grpc.Core;
using Grpc.Net.Client;

namespace AnyProtocol.Protocol.Grpc;

/// <summary>
/// Implements grpc messaging messaging transport operations.
/// </summary>
public sealed class GrpcMessagingProtocol :
    IMessagingProtocol,
    INativeRequestReplyTransport,
    INativeStreamingTransport,
    ITransportReadiness
{
    private readonly CallInvoker _callInvoker;
    private readonly GrpcChannel? _channel;
    private readonly GrpcChannel? _ownedChannel;
    private readonly IEnvelopeCodec _codec;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the GrpcMessagingProtocol class.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="disposeChannel">The dispose channel.</param>
    public GrpcMessagingProtocol(GrpcChannel channel, bool disposeChannel = false)
        : this(channel, new BinaryEnvelopeCodec(), disposeChannel)
    {
    }

    /// <summary>
    /// Initializes a new instance of the GrpcMessagingProtocol class.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="codec">The envelope codec.</param>
    /// <param name="disposeChannel">The dispose channel.</param>
    public GrpcMessagingProtocol(
        GrpcChannel channel,
        IEnvelopeCodec codec,
        bool disposeChannel = false)
        : this(channel?.CreateCallInvoker() ?? throw new ArgumentNullException(nameof(channel)), codec)
    {
        _channel = channel;
        _ownedChannel = disposeChannel ? channel : null;
    }

    /// <summary>
    /// Initializes a new instance of the GrpcMessagingProtocol class.
    /// </summary>
    /// <param name="callInvoker">The call invoker.</param>
    public GrpcMessagingProtocol(CallInvoker callInvoker)
        : this(callInvoker, new BinaryEnvelopeCodec())
    {
    }

    /// <summary>
    /// Initializes a new instance of the GrpcMessagingProtocol class.
    /// </summary>
    /// <param name="callInvoker">The call invoker.</param>
    /// <param name="codec">The envelope codec.</param>
    public GrpcMessagingProtocol(CallInvoker callInvoker, IEnvelopeCodec codec)
    {
        _callInvoker = callInvoker ?? throw new ArgumentNullException(nameof(callInvoker));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>
    /// Gets the optional transport capabilities supported by this protocol.
    /// </summary>
    /// <value>The capabilities.</value>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeHeaders |
        TransportCapabilities.NativeRequestReply |
        TransportCapabilities.NativeStreaming;

    /// <summary>
    /// Gets the delivery and ordering guarantees provided by this protocol.
    /// </summary>
    /// <value>The semantics.</value>
    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.None,
        Durability = TransportDurability.Volatile,
        SupportsNativeRequestReply = true,
        SupportsNativeStreaming = true,
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
        _ = await RequestAsync(channel, envelope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the request async.</returns>
    public async ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        request.Headers[HeaderNames.Channel] ??= channel;

        try
        {
            var call = _callInvoker.AsyncUnaryCall(
                GrpcTransportMethods.Unary,
                host: null,
                CreateCallOptions(request, cancellationToken),
                _codec.Encode(request).ToArray());
            var response = await call.ResponseAsync.ConfigureAwait(false);
            return _codec.Decode(response);
        }
        catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The gRPC request was cancelled.",
                exception,
                cancellationToken);
        }
        catch (RpcException exception)
        {
            throw new IOException(
                $"gRPC request failed with status '{exception.StatusCode}': {exception.Status.Detail}",
                exception);
        }
    }

    /// <summary>
    /// Performs the stream async operation.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="request">The request to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of values produced by the operation.</returns>
    public async IAsyncEnumerable<TransportEnvelope> StreamAsync(
        string channel,
        TransportEnvelope request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        request.Headers[HeaderNames.Channel] ??= channel;

        AsyncServerStreamingCall<byte[]>? call = null;
        try
        {
            call = _callInvoker.AsyncServerStreamingCall(
                GrpcTransportMethods.ServerStream,
                host: null,
                CreateCallOptions(request, cancellationToken),
                _codec.Encode(request).ToArray());
            while (true)
            {
                bool hasNext;
                Exception? error = null;
                try
                {
                    hasNext = await call.ResponseStream.MoveNext(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RpcException exception)
                {
                    hasNext = false;
                    error = exception;
                }

                if (error is RpcException deadline &&
                    (deadline.StatusCode == StatusCode.DeadlineExceeded ||
                     HasElapsedDeadline(request, cancellationToken)))
                {
                    throw new TimeoutException("The gRPC stream deadline was exceeded.", deadline);
                }

                if (error is RpcException cancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "The gRPC stream was cancelled.",
                        cancelled,
                        cancellationToken);
                }

                if (error is RpcException rpcError)
                {
                    throw new IOException(
                        $"gRPC stream failed with status '{rpcError.StatusCode}': " +
                        rpcError.Status.Detail,
                        rpcError);
                }

                if (!hasNext)
                {
                    break;
                }

                yield return _codec.Decode(call.ResponseStream.Current);
            }
        }
        finally
        {
            call?.Dispose();
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
        => throw new NotSupportedException("gRPC client transport does not support subscriptions.");

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

        if (_ownedChannel is not null)
        {
            await _ownedChannel.ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks whether the transport is ready to handle messages without mutating application state.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the check readiness async.</returns>
    public async ValueTask<TransportReadinessResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return TransportReadinessResult.NotReady("The gRPC transport is disposed.");
        }

        if (_channel is null)
        {
            return TransportReadinessResult.Unknown(
                "The supplied gRPC CallInvoker does not expose channel connectivity.");
        }

        try
        {
            await _channel.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return _channel.State == ConnectivityState.Ready
                ? TransportReadinessResult.Ready("The gRPC channel is connected.")
                : TransportReadinessResult.NotReady(
                    $"The gRPC channel is {_channel.State.ToString().ToLowerInvariant()}.");
        }
        catch (Exception exception) when (
            exception is RpcException or HttpRequestException or IOException)
        {
            return TransportReadinessResult.NotReady("The gRPC channel could not connect.");
        }
    }

    private static CallOptions CreateCallOptions(
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        DateTime? deadline = null;
        var rawDeadline = envelope.Headers[HeaderNames.Deadline];
        if (DateTimeOffset.TryParse(rawDeadline, out var parsedDeadline))
        {
            deadline = parsedDeadline.UtcDateTime;
        }

        return new CallOptions(deadline: deadline, cancellationToken: cancellationToken);
    }

    private static bool HasElapsedDeadline(
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
        => !cancellationToken.IsCancellationRequested &&
           DateTimeOffset.TryParse(envelope.Headers[HeaderNames.Deadline], out var deadline) &&
           deadline <= DateTimeOffset.UtcNow;
}
