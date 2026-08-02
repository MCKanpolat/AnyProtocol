using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using Grpc.Core;

namespace AnyProtocol.Protocol.Grpc.AspNetCore;

/// <summary>
/// Provides the anyprotocol grpc service implementation used by AnyProtocol applications.
/// </summary>
[BindServiceMethod(typeof(AnyProtocolGrpcService), nameof(BindService))]
public class AnyProtocolGrpcService
{
    private const string ReplyChannel = "_anyprotocol.grpc.response";
    private readonly LinkConfiguration _configuration;
    private readonly ContractDescriptorFactory _descriptorFactory;
    private readonly TransportRegistry _registry;
    private readonly MessageDispatcher _dispatcher;
    private readonly BinaryEnvelopeCodec _codec = new();

    /// <summary>
    /// Initializes a new instance of the AnyProtocolGrpcService class.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    public AnyProtocolGrpcService(
        LinkConfiguration configuration,
        ContractDescriptorFactory descriptorFactory,
        TransportRegistry registry,
        MessageDispatcher dispatcher)
    {
        _configuration = configuration;
        _descriptorFactory = descriptorFactory;
        _registry = registry;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Performs the bind service operation.
    /// </summary>
    /// <param name="binder">The binder.</param>
    /// <param name="service">The service.</param>
    public static void BindService(
        ServiceBinderBase binder,
        AnyProtocolGrpcService service)
    {
        binder.AddMethod(
            GrpcTransportMethods.Unary,
            service is null ? null : service.Unary);
        binder.AddMethod(
            GrpcTransportMethods.ServerStream,
            service is null ? null : service.ServerStream);
    }

    /// <summary>
    /// Performs the unary operation.
    /// </summary>
    /// <param name="frame">The frame to process.</param>
    /// <param name="callContext">The context for the current operation.</param>
    /// <returns>A task whose result contains the unary.</returns>
    public virtual async Task<byte[]> Unary(byte[] frame, ServerCallContext callContext)
    {
        var request = _codec.Decode(frame);
        var capture = new CaptureProtocol();
        request.Headers[HeaderNames.ReplyTo] = ReplyChannel;
        var route = FindRoute(request);
        var eventRoute = route is null &&
                         request.Headers.Get(HeaderNames.MessageType, MessageType.Request) ==
                         MessageType.Event
            ? FindEventRoute(request)
            : null;
        if (eventRoute is not null)
        {
            await _dispatcher.DispatchEventAsync(
                    eventRoute,
                    request,
                    capture,
                    callContext.CancellationToken)
                .ConfigureAwait(false);
        }
        else if (route is null || route.Method.Operation == ContractOperation.Stream)
        {
            await _dispatcher.DispatchRoutingFaultAsync(
                    request,
                    capture,
                    $"No unary gRPC route matches '{request.Headers[HeaderNames.Contract]}." +
                    $"{request.Headers[HeaderNames.Method]}'.",
                    callContext.CancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await _dispatcher.DispatchAsync(
                    route.Registration,
                    route.Method,
                    request,
                    capture,
                    callContext.CancellationToken,
                    route.Protocol)
                .ConfigureAwait(false);
        }

        var response = capture.Response ?? new TransportEnvelope(
            CreateHeaders(request, MessageType.Response),
            ReadOnlyMemory<byte>.Empty);
        return _codec.Encode(response).ToArray();
    }

    /// <summary>
    /// Performs the server stream operation.
    /// </summary>
    /// <param name="frame">The frame to process.</param>
    /// <param name="responseStream">The response stream.</param>
    /// <param name="callContext">The context for the current operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public virtual async Task ServerStream(
        byte[] frame,
        IServerStreamWriter<byte[]> responseStream,
        ServerCallContext callContext)
    {
        var request = _codec.Decode(frame);
        var route = FindRoute(request);
        if (route is null || route.Method.Operation != ContractOperation.Stream)
        {
            var capture = new CaptureProtocol();
            request.Headers[HeaderNames.ReplyTo] = ReplyChannel;
            await _dispatcher.DispatchRoutingFaultAsync(
                    request,
                    capture,
                    $"No streaming gRPC route matches '{request.Headers[HeaderNames.Contract]}." +
                    $"{request.Headers[HeaderNames.Method]}'.",
                    callContext.CancellationToken)
                .ConfigureAwait(false);
            await responseStream.WriteAsync(
                    _codec.Encode(capture.Response!).ToArray(),
                    callContext.CancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var transport = _registry.GetRequired(route.Protocol);
        try
        {
            await foreach (var response in _dispatcher.DispatchStreamAsync(
                                   route.Registration,
                                   route.Method,
                                   request,
                                   transport,
                                   callContext.CancellationToken,
                                   route.Protocol)
                               .ConfigureAwait(false))
            {
                await responseStream.WriteAsync(
                        _codec.Encode(response).ToArray(),
                        callContext.CancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception)
        {
            var statusCode = callContext.Deadline != DateTime.MaxValue &&
                             callContext.Deadline <= DateTime.UtcNow.AddMilliseconds(100)
                ? StatusCode.DeadlineExceeded
                : StatusCode.Cancelled;
            throw new RpcException(
                new Status(statusCode, "The AnyProtocol stream was cancelled."),
                exception.Message);
        }
    }

    private Route? FindRoute(TransportEnvelope request)
    {
        var contract = request.Headers[HeaderNames.Contract];
        var method = request.Headers[HeaderNames.Method];
        return _configuration.ServerRegistrations
            .SelectMany(
                registration => registration.Protocols
                    .Where(protocol =>
                        _registry.TryGet(protocol, out var transport) &&
                        transport is GrpcServerProtocol)
                    .SelectMany(
                        protocol => _descriptorFactory.Create(registration.ContractType).Methods
                            .Select(descriptor => new Route(registration, descriptor, protocol))))
            .SingleOrDefault(
                route =>
                    route.Method.ContractName == contract &&
                    route.Method.MethodName == method);
    }

    private EventRegistration? FindEventRoute(TransportEnvelope request)
    {
        var contract = request.Headers[HeaderNames.Contract];
        var channel = request.Headers[HeaderNames.Channel];
        return _configuration.EventRegistrations
            .Where(
                registration =>
                    _registry.GetRequired(registration.TransportName) is GrpcServerProtocol)
            .SingleOrDefault(
                registration =>
                    registration.Channel == channel &&
                    (registration.EventType.FullName ?? registration.EventType.Name) == contract);
    }

    private static MessageHeaders CreateHeaders(
        TransportEnvelope request,
        MessageType messageType)
        => new()
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.CorrelationId] =
                request.Headers[HeaderNames.CorrelationId] ??
                request.Headers[HeaderNames.MessageId],
            [HeaderNames.MessageType] = messageType.ToString(),
            [HeaderNames.ContentType] = request.Headers[HeaderNames.ContentType]
        };

    private sealed record Route(
        ServerRegistration Registration,
        ContractMethodDescriptor Method,
        ProtocolKey Protocol);

    private sealed class CaptureProtocol : IMessagingProtocol
    {
        public TransportEnvelope? Response { get; private set; }

        public TransportCapabilities Capabilities =>
            TransportCapabilities.NativeRequestReply |
            TransportCapabilities.NativeHeaders;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            if (channel != ReplyChannel)
            {
                throw new InvalidOperationException($"Unexpected gRPC reply channel '{channel}'.");
            }

            Response = envelope;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
