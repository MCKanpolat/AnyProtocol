using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Encoder.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Services;
using Grpc.Core;

namespace AnyProtocol.Protocol.Grpc.AspNetCore;

/// <summary>
/// Provides the anyprotocol grpc service implementation used by AnyProtocol applications.
/// </summary>
[BindServiceMethod(typeof(AnyProtocolGrpcService), nameof(BindService))]
public class AnyProtocolGrpcService
{
    private const string ReplyChannel = "_anyprotocol.grpc.response";
    private readonly RuntimePlan _runtimePlan;
    private readonly TransportRegistry _registry;
    private readonly MessageDispatcher _dispatcher;
    private readonly IEnvelopeCodec _codec;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly IRequestAdmission _admission;

    /// <summary>
    /// Initializes a new instance of the AnyProtocolGrpcService class.
    /// </summary>
    /// <param name="runtimePlan">The immutable runtime plan.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="admission">The shared request admission coordinator.</param>
    public AnyProtocolGrpcService(
        RuntimePlan runtimePlan,
        TransportRegistry registry,
        MessageDispatcher dispatcher,
        IMessageEnvelopeFactory? envelopeFactory = null,
        IRequestAdmission? admission = null)
        : this(
            runtimePlan,
            registry,
            dispatcher,
            new BinaryEnvelopeCodec(),
            envelopeFactory,
            admission)
    {
    }

    /// <summary>
    /// Initializes a new instance of the AnyProtocolGrpcService class.
    /// </summary>
    /// <param name="runtimePlan">The immutable runtime plan.</param>
    /// <param name="registry">The registry.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="codec">The envelope codec.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="admission">The shared request admission coordinator.</param>
    public AnyProtocolGrpcService(
        RuntimePlan runtimePlan,
        TransportRegistry registry,
        MessageDispatcher dispatcher,
        IEnvelopeCodec codec,
        IMessageEnvelopeFactory? envelopeFactory = null,
        IRequestAdmission? admission = null)
    {
        _runtimePlan = runtimePlan ?? throw new ArgumentNullException(nameof(runtimePlan));
        _registry = registry;
        _dispatcher = dispatcher;
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
        _admission = admission ?? CreateStandaloneAdmission();
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
        using var admissionLease = _admission.TryEnter();
        if (admissionLease is null)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "AnyProtocol is draining."));
        }

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
                    eventRoute.Registration,
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
        using var admissionLease = _admission.TryEnter();
        if (admissionLease is null)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "AnyProtocol is draining."));
        }

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
                             callContext.Deadline.ToUniversalTime() <=
                             _envelopeFactory.GetUtcNow().UtcDateTime.AddMilliseconds(100)
                ? StatusCode.DeadlineExceeded
                : StatusCode.Cancelled;
            throw new RpcException(
                new Status(statusCode, "The AnyProtocol stream was cancelled."),
                exception.Message);
        }
    }

    private ServerRoutePlan? FindRoute(TransportEnvelope request)
    {
        var contract = request.Headers[HeaderNames.Contract];
        var method = request.Headers[HeaderNames.Method];
        return _runtimePlan.ServerRoutes
            .Where(route =>
                _registry.TryGet(route.Protocol, out var transport) &&
                transport is GrpcServerProtocol)
            .SingleOrDefault(
                route =>
                    route.Method.ContractName == contract &&
                    route.Method.MethodName == method);
    }

    private EventPlan? FindEventRoute(TransportEnvelope request)
    {
        var contract = request.Headers[HeaderNames.Contract];
        var channel = request.Headers[HeaderNames.Channel];
        return _runtimePlan.EventPlans
            .Where(
                eventPlan =>
                    _registry.GetRequired(eventPlan.Registration.TransportName) is GrpcServerProtocol)
            .SingleOrDefault(
                eventPlan =>
                    eventPlan.Registration.Channel == channel &&
                    (eventPlan.Registration.EventType.FullName ?? eventPlan.Registration.EventType.Name) == contract);
    }

    private MessageHeaders CreateHeaders(
        TransportEnvelope request,
        MessageType messageType)
        => new(_envelopeFactory.CreateResponseHeaders(request.Headers, messageType));

    private static RequestAdmissionCoordinator CreateStandaloneAdmission()
    {
        var admission = new RequestAdmissionCoordinator();
        admission.StartAccepting();
        return admission;
    }

    private sealed class CaptureProtocol : ISendTransport
    {
        public TransportEnvelope? Response { get; private set; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

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

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
