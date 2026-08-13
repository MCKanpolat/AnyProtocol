using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AnyProtocol;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Mcp;
using AnyProtocol.Services;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;

var descriptorFactory = new ContractDescriptorFactory();
using var serializer = new TextJsonMessageSerializer(SampleJsonContext.Default);

if (!GeneratedContractRegistry.IsComplete(typeof(IGreetingService)))
{
    throw new InvalidOperationException(
        $"Generated registration for '{typeof(IGreetingService).FullName}' is incomplete.");
}

GeneratedContractRegistry.Validate(typeof(IGreetingService), serializer);
var descriptor = descriptorFactory.CreateGenerated(typeof(IGreetingService));
var greetingMethod = descriptor.Methods.Single(
    static method => method.MethodName == nameof(IGreetingService.GreetAsync));
var streamMethod = descriptor.Methods.Single(
    static method => method.MethodName == nameof(IGreetingService.StreamAsync));
Require(greetingMethod.IsIdempotent, "Generated [Idempotent] metadata was not preserved.");
Require(
    greetingMethod.FaultType == typeof(GreetingFault),
    "Generated fault metadata was not preserved.");
Require(
    streamMethod.Operation == ContractOperation.Stream,
    "Generated streaming metadata was not preserved.");
Require(
    GeneratedServerDispatchRegistry.IsRegistered(
        typeof(IGreetingService),
        nameof(IGreetingService.GreetAsync)),
    "Generated request/reply server dispatch was not registered.");
Require(
    GeneratedServerDispatchRegistry.IsRegistered(
        typeof(IGreetingService),
        nameof(IGreetingService.StreamAsync)),
    "Generated streaming server dispatch was not registered.");
Require(
    GeneratedEventDispatchRegistry.IsRegistered(
        typeof(GreetingPublished),
        typeof(GreetingPublishedHandler)),
    "Generated event dispatch was not registered.");

var serializableTypes = GeneratedContractMetadataRegistry.GetSerializableTypes(
    typeof(IGreetingService));
foreach (var expectedType in new[]
         {
             typeof(GreetingRequest),
             typeof(GreetingResponse),
             typeof(GreetingFault),
             typeof(FaultMessage),
             typeof(Unit),
             typeof(object)
         })
{
    Require(
        serializableTypes.Contains(expectedType),
        $"Generated serializer metadata does not include '{expectedType.FullName}'.");
}

var proxy = new GeneratedContractProxyFactory(descriptorFactory)
    .Create<IGreetingService>(new GreetingInvoker());
var request = new GreetingRequest("Native AOT");
var response = await proxy.GreetAsync(request, CancellationToken.None);
Require(response.Message == "Hello, Native AOT!", "Generated request/reply proxy failed.");

var streamed = new List<string>();
await foreach (var item in proxy.StreamAsync(request, CancellationToken.None))
{
    streamed.Add(item.Message);
}

Require(
    streamed.SequenceEqual(["Hello, Native AOT #1!", "Hello, Native AOT #2!"]),
    "Generated streaming proxy failed.");

RoundTrip(serializer, request);
RoundTrip(serializer, response);
var fault = RoundTrip(serializer, new GreetingFault("sample_fault", "Expected failure."));
Require(fault.Code == "sample_fault", "Fault serialization failed.");

await using var transport = new InMemoryMessagingProtocol();
var configuration = new LinkBuilder()
    .UseSerializer(serializer)
    .AddTransport("default", transport)
    .AddClient<IGreetingService>()
    .AddServer<IGreetingService, GreetingService>(server => server.UseProtocols(
        ProtocolKey.Default,
        ProtocolKey.Mcp))
    .AddEventHandler<GreetingPublished, GreetingPublishedHandler>()
    .Build(descriptorFactory);
var catalog = new McpToolCatalog(
    configuration,
    SampleJsonContext.Default.Options);
var tool = catalog.GetRequired("greeting_greet");
Require(tool.Idempotent, "MCP discovery did not preserve idempotency.");
Require(
    tool.InputSchema.GetProperty("type").GetString() == "object",
    "MCP input schema discovery failed.");
Require(
    tool.OutputSchema?.GetProperty("type").GetString() == "object",
    "MCP output schema discovery failed.");

var service = new GreetingService();
var eventHandler = new GreetingPublishedHandler();
var resolverFactory = new StaticResolverFactory(
    (typeof(GreetingService), service),
    (typeof(GreetingPublishedHandler), eventHandler));
var dispatcher = new MessageDispatcher(
    resolverFactory,
    serializer,
    envelopeFactory: DefaultMessageEnvelopeFactory.CreateDefault());
var registration = configuration.ServerRegistrations.Single();
const string replyChannel = "_sample.reply";
var serverCapture = new CaptureProtocol(replyChannel);
var serverRequest = new GreetingRequest("Server");
await dispatcher.DispatchAsync(
    registration,
    greetingMethod,
    CreateRequestEnvelope(
        greetingMethod,
        serializer.Serialize(serverRequest),
        replyChannel),
    serverCapture);
var serverResponse = serializer.Deserialize<GreetingResponse>(
    serverCapture.Response?.Body ??
    throw new InvalidOperationException("Server request produced no response."));
Require(
    serverResponse?.Message == "Hello, Server!",
    "Generated request/reply server dispatch failed.");

var streamEnvelopes = new List<TransportEnvelope>();
await foreach (var item in dispatcher.DispatchStreamAsync(
                   registration,
                   streamMethod,
                   CreateRequestEnvelope(
                       streamMethod,
                       serializer.Serialize(new GreetingRequest("fail")),
                       replyChannel),
                   serverCapture))
{
    streamEnvelopes.Add(item);
}

Require(
    streamEnvelopes.Count == 2 &&
    streamEnvelopes[0].Headers.Get(HeaderNames.MessageType, MessageType.Fault) ==
    MessageType.StreamItem &&
    streamEnvelopes[1].Headers.Get(HeaderNames.MessageType, MessageType.Response) ==
    MessageType.Fault,
    "Generated streaming server dispatch did not produce item and fault envelopes.");
var streamFault = serializer.Deserialize<FaultMessage>(streamEnvelopes[1].Body);
Require(
    streamFault?.Code == "handler_failed" &&
    streamFault.Message.Contains("Expected stream failure", StringComparison.Ordinal),
    "Generated streaming server fault serialization failed.");

var mcpInvoker = new McpToolInvoker(
    catalog,
    dispatcher,
    serializer,
    new EmptyMcpCredentialProvider(),
    DefaultMessageEnvelopeFactory.CreateDefault());
var mcpResult = await mcpInvoker.InvokeAsync(
    "greeting_greet",
    new Dictionary<string, JsonElement>
    {
        ["Name"] = ParseJsonElement("\"MCP\"")
    });
Require(
    !mcpResult.IsError &&
    mcpResult.StructuredContent?.GetProperty("Message").GetString() == "Hello, MCP!",
    "MCP tool invocation did not execute the generated server dispatcher.");

var mcpFault = await mcpInvoker.InvokeAsync(
    "greeting_fail",
    new Dictionary<string, JsonElement>
    {
        ["Name"] = ParseJsonElement("\"MCP\"")
    });
Require(
    mcpFault.IsError && mcpFault.ErrorCode == "sample_fault",
    "MCP tool fault did not execute through the dispatcher.");

var admission = new RequestAdmissionCoordinator();
await using var bus = new AnyProtocolBus(
    configuration,
    new TransportRegistry(
        [new KeyValuePair<ProtocolKey, IMessagingProtocol>(ProtocolKey.Default, transport)]),
    dispatcher,
    admission);
await bus.StartAsync();
var eventRegistration = configuration.EventRegistrations.Single();
var publisher = new EventPublisher<GreetingPublished>(
    transport,
    serializer,
    eventRegistration.Channel,
    eventRegistration.Protocol.Value,
    new OutboundOperationExecutor(resolverFactory, admission));
var published = new GreetingPublished(Guid.NewGuid(), "Native AOT event");
await publisher.PublishAsync(published);
var consumed = await eventHandler.Received.WaitAsync(TimeSpan.FromSeconds(5));
Require(consumed == published, "Generated event publish/consume dispatch failed.");

Console.WriteLine("AnyProtocol Native AOT smoke test passed.");

static TransportEnvelope CreateRequestEnvelope(
    ContractMethodDescriptor method,
    ReadOnlyMemory<byte> body,
    string replyChannel)
    => new(
        new MessageHeaders
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.CorrelationId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.ReplyTo] = replyChannel,
            [HeaderNames.Channel] = method.Channel,
            [HeaderNames.Contract] = method.ContractName,
            [HeaderNames.Method] = method.MethodName,
            [HeaderNames.MessageType] = MessageType.Request.ToString()
        },
        body);

static JsonElement ParseJsonElement(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

static T RoundTrip<T>(TextJsonMessageSerializer serializer, T value)
{
    var payload = serializer.Serialize(value);
    return serializer.Deserialize<T>(payload) ??
           throw new InvalidOperationException(
               $"'{typeof(T).FullName}' could not be deserialized.");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

public sealed record GreetingRequest(string Name);

public sealed record GreetingResponse(string Message);

public sealed record GreetingFault(string Code, string Message);

public sealed record GreetingPublished(Guid Id, string Name);

public interface IGreetingService
{
    [Idempotent]
    [McpTool(
        Name = "greeting_greet",
        Description = "Creates a greeting.",
        ReadOnly = true,
        Idempotent = true)]
    [FaultContract(typeof(GreetingFault))]
    ValueTask<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken);

    [McpTool(Name = "greeting_fail", Description = "Produces an expected fault.")]
    ValueTask<GreetingResponse> FailAsync(GreetingRequest request);

    IAsyncEnumerable<GreetingResponse> StreamAsync(
        GreetingRequest request,
        CancellationToken cancellationToken);
}

public sealed class GreetingService : IGreetingService
{
    public ValueTask<GreetingResponse> GreetAsync(
        GreetingRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));

    public ValueTask<GreetingResponse> FailAsync(GreetingRequest request)
        => throw new AnyProtocolFaultException(
            new FaultMessage("sample_fault", $"Expected failure for {request.Name}."));

    public async IAsyncEnumerable<GreetingResponse> StreamAsync(
        GreetingRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new GreetingResponse($"Hello, {request.Name} #1!");
        if (request.Name == "fail")
        {
            throw new InvalidOperationException("Expected stream failure.");
        }

        yield return new GreetingResponse($"Hello, {request.Name} #2!");
    }
}

public sealed class GreetingPublishedHandler : IEventConsumer<GreetingPublished>
{
    private readonly TaskCompletionSource<GreetingPublished> _received = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<GreetingPublished> Received => _received.Task;

    public ValueTask ConsumeAsync(GreetingPublished @event)
    {
        _received.TrySetResult(@event);
        return ValueTask.CompletedTask;
    }
}

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
[JsonSerializable(typeof(GreetingFault))]
[JsonSerializable(typeof(GreetingPublished))]
[JsonSerializable(typeof(FaultMessage))]
[JsonSerializable(typeof(Unit))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonNode))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

internal sealed class GreetingInvoker : IClientInvoker
{
    public ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        var greeting = (GreetingRequest)(object)request!;
        return ValueTask.FromResult(
            (TResponse)(object)new GreetingResponse($"Hello, {greeting.Name}!"));
    }

    public ValueTask SendAsync<TRequest>(
        ContractMethodDescriptor method,
        TRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"The smoke invoker does not support send method '{method.MethodName}'.");

    public async IAsyncEnumerable<TItem> StreamAsync<TRequest, TItem>(
        ContractMethodDescriptor method,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var greeting = (GreetingRequest)(object)request!;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return (TItem)(object)new GreetingResponse($"Hello, {greeting.Name} #1!");
        yield return (TItem)(object)new GreetingResponse($"Hello, {greeting.Name} #2!");
    }
}

internal sealed class StaticResolverFactory : IDependencyResolverFactory
{
    private readonly IReadOnlyDictionary<Type, object> _services;

    public StaticResolverFactory(params (Type ServiceType, object Instance)[] services)
    {
        _services = services.ToDictionary(
            static service => service.ServiceType,
            static service => service.Instance);
    }

    public IDependencyResolver CreateResolver() => new Resolver(_services);

    public IAsyncDependencyScope CreateAsyncScope() => new Scope(CreateResolver());

    private sealed class Resolver(IReadOnlyDictionary<Type, object> services) : IDependencyResolver
    {
        public TService? Resolve<TService>() where TService : class
            => Resolve(typeof(TService)) as TService;

        public object? Resolve(Type requestedType)
            => services.GetValueOrDefault(requestedType);

        public IEnumerable<TService> ResolveAll<TService>() where TService : class
            => Resolve<TService>() is { } resolved ? [resolved] : [];

        public IEnumerable<object?> ResolveAll(Type requestedType)
            => Resolve(requestedType) is { } resolved ? [resolved] : [];
    }

    private sealed class Scope(IDependencyResolver resolver) : IAsyncDependencyScope
    {
        public IDependencyResolver Resolver => resolver;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class CaptureProtocol(string replyChannel) : ISendTransport
{
    public TransportEnvelope? Response { get; private set; }

    public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

    public ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (channel != replyChannel)
        {
            throw new InvalidOperationException($"Unexpected reply channel '{channel}'.");
        }

        Response = envelope;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
