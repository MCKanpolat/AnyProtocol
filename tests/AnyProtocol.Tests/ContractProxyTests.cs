using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using System.Text.Json.Serialization;
using Xunit;

namespace AnyProtocol.Tests;

public sealed record ProxyRequest(string Value);

public sealed record ProxyResponse(string Value);

public sealed record PartitionedRequest([property: PartitionKey] string CustomerId, string Value);

public sealed record InvalidPartitionedRequest(
    [property: PartitionKey] string CustomerId,
    [property: PartitionKey] string OrderId);

public interface IPartitionedContract
{
    ValueTask PublishAsync(PartitionedRequest request);
}

[RequirePermission("orders.read")]
public interface IProxyContract
{
    ValueTask<ProxyResponse> GetAsync(ProxyRequest request, CancellationToken cancellationToken);

    Task<ProxyResponse> GetTaskAsync(ProxyRequest request);

    ValueTask NotifyAsync(ProxyRequest request);

    Task NotifyTaskAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<ProxyResponse> WatchAsync(ProxyRequest request, CancellationToken cancellationToken);
}

public interface IBaseProxyContract
{
    ValueTask<ProxyResponse> BaseAsync(ProxyRequest request);
}

public interface IDerivedProxyContract : IBaseProxyContract
{
    ValueTask<ProxyResponse> DerivedAsync(ProxyRequest request);
}

public interface IJitFallbackContract
{
    ValueTask NotifyAsync();
}

[JsonSerializable(typeof(string))]
internal sealed partial class IncompleteContractJsonContext : JsonSerializerContext;

public sealed class ContractProxyTests
{
    [Fact]
    public void Descriptor_builds_conventional_channels_and_metadata()
    {
        var descriptor = new ContractDescriptorFactory("app").Create<IProxyContract>();
        var method = descriptor.Methods.Single(item => item.MethodName == nameof(IProxyContract.GetAsync));

        Assert.Equal("app.proxy-contract.get", method.Channel);
        Assert.Equal(typeof(ProxyRequest), method.RequestType);
        Assert.Equal(typeof(ProxyResponse), method.ResponseType);
        Assert.Equal(ContractOperation.Request, method.Operation);
        Assert.Contains("orders.read", method.RequiredPermissions);
    }

    [Fact]
    public void Descriptor_rejects_multiple_payload_parameters()
    {
        var exception = Assert.Throws<ContractShapeException>(
            () => new ContractDescriptorFactory().Create(typeof(IInvalidContract)));

        Assert.Contains("zero or one payload", exception.Message);
    }

    [Fact]
    public void Descriptor_includes_inherited_interface_methods()
    {
        var descriptor = new ContractDescriptorFactory().Create<IDerivedProxyContract>();

        Assert.Contains(descriptor.Methods, method => method.MethodName == "BaseAsync");
        Assert.Contains(descriptor.Methods, method => method.MethodName == "DerivedAsync");
    }

    [Fact]
    public async Task Partition_key_attribute_is_validated_and_propagated_to_transport_headers()
    {
        var descriptor = new ContractDescriptorFactory().Create<IPartitionedContract>();
        var method = Assert.Single(descriptor.Methods);
        var transport = new CapturingTransport();
        await using var invoker = new AnyProtocolClientInvoker(
            new TransportRegistry(
                [new KeyValuePair<string, IMessagingProtocol>("kafka", transport)]),
            new TextJsonMessageSerializer(),
            [
                new ClientRegistration(
                    typeof(IPartitionedContract),
                    "kafka",
                    TimeSpan.FromSeconds(1),
                    1)
            ]);

        await invoker.SendAsync(method, new PartitionedRequest("customer-42", "created"));

        Assert.Equal("CustomerId", method.PartitionKeyProperty?.Name);
        Assert.Equal("customer-42", transport.Envelope?.Headers[HeaderNames.PartitionKey]);
    }

    [Fact]
    public void Descriptor_rejects_multiple_partition_keys()
    {
        var exception = Assert.Throws<ContractShapeException>(
            () => new ContractDescriptorFactory().Create<IInvalidPartitionedContract>());

        Assert.Contains("more than one [PartitionKey]", exception.Message);
    }

    [Fact]
    public async Task Emitted_proxy_dispatches_all_supported_return_shapes()
    {
        var invoker = new RecordingInvoker();
        var proxy = new EmittedContractProxyFactory(new ContractDescriptorFactory())
            .Create<IProxyContract>(invoker);
        using var cancellation = new CancellationTokenSource();

        var valueTaskResponse = await proxy.GetAsync(new ProxyRequest("value-task"), cancellation.Token);
        var taskResponse = await proxy.GetTaskAsync(new ProxyRequest("task"));
        await proxy.NotifyAsync(new ProxyRequest("notify"));
        await proxy.NotifyTaskAsync(cancellation.Token);
        var streamed = new List<ProxyResponse>();
        await foreach (var item in proxy.WatchAsync(new ProxyRequest("stream"), cancellation.Token))
        {
            streamed.Add(item);
        }

        Assert.Equal("value-task", valueTaskResponse.Value);
        Assert.Equal("task", taskResponse.Value);
        Assert.Equal("stream", Assert.Single(streamed).Value);
        Assert.Equal(5, invoker.Calls.Count);
        Assert.Equal(cancellation.Token, invoker.Calls[0].CancellationToken);
        Assert.IsType<EmptyRequest>(invoker.Calls[3].Request);
    }

    [Fact]
    public async Task Generated_factory_prefers_source_generated_proxy()
    {
        Assert.True(GeneratedContractProxyRegistry.IsRegistered(typeof(IOrderService)));
        var invoker = new RecordingOrderInvoker();
        var proxy = new GeneratedContractProxyFactory(new ContractDescriptorFactory())
            .Create<IOrderService>(invoker);

        var response = await proxy.PlaceAsync(
            new PlaceOrderRequest("generated"),
            CancellationToken.None);

        Assert.Equal("generated", response.OrderId);
        Assert.StartsWith("AnyProtocol.Generated.", proxy.GetType().FullName);
    }

    [Fact]
    public void Generated_registry_validates_complete_registration_and_serializer_metadata()
    {
        using var serializer = new TextJsonMessageSerializer();

        Assert.True(GeneratedContractRegistry.IsComplete(typeof(IOrderService)));
        Assert.True(
            GeneratedServerDispatchRegistry.IsRegistered(
                typeof(IOrderService),
                nameof(IOrderService.PlaceAsync)));
        GeneratedContractRegistry.Validate(typeof(IOrderService), serializer);
    }

    [Fact]
    public void Generated_registry_reports_missing_contract_components()
    {
        using var serializer = new TextJsonMessageSerializer();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GeneratedContractRegistry.Validate(typeof(IUnregisteredContract), serializer));

        Assert.Contains(typeof(IUnregisteredContract).FullName!, exception.Message);
        Assert.Contains("descriptor", exception.Message);
        Assert.Contains("proxy", exception.Message);
        Assert.Contains("serializer metadata", exception.Message);
        Assert.Contains("server dispatch", exception.Message);
        Assert.Contains("AddClient<TContract>", exception.Message);
    }

    [Fact]
    public void Generated_registry_reports_missing_serializer_type_metadata()
    {
        using var serializer = new TextJsonMessageSerializer(IncompleteContractJsonContext.Default);

        var exception = Assert.Throws<InvalidOperationException>(
            () => GeneratedContractRegistry.Validate(typeof(IOrderService), serializer));

        Assert.Contains(typeof(IOrderService).FullName!, exception.Message);
        Assert.Contains(nameof(PlaceOrderRequest), exception.Message);
        Assert.Contains("source-generated metadata", exception.Message);
    }

    [Fact]
    public void Generated_registry_reports_legacy_registration_missing_server_dispatch()
    {
        GeneratedContractRegistry.Register(
            typeof(ILegacyGeneratedContract),
            _ => new ContractDescriptorFactory().Create(typeof(ILegacyGeneratedContract)),
            [typeof(EmptyRequest), typeof(Unit), typeof(FaultMessage), typeof(object)],
            (_, _) => throw new NotSupportedException());
        using var serializer = new TextJsonMessageSerializer();

        var exception = Assert.Throws<InvalidOperationException>(
            () => GeneratedContractRegistry.Validate(
                typeof(ILegacyGeneratedContract),
                serializer));

        Assert.Contains(typeof(ILegacyGeneratedContract).FullName!, exception.Message);
        Assert.Contains("server dispatch", exception.Message);
    }

    [Fact]
    public void Generated_registry_rejects_duplicate_atomic_registration()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => GeneratedContractRegistry.Register(
                typeof(IOrderService),
                _ => throw new NotSupportedException(),
                [],
                [],
                (_, _) => throw new NotSupportedException()));

        Assert.Contains(typeof(IOrderService).FullName!, exception.Message);
        Assert.Contains("only one referenced assembly", exception.Message);
    }

    [Fact]
    public void Runtime_compatible_factories_preserve_jit_fallback()
    {
        var descriptorFactory = new ContractDescriptorFactory();
        var descriptor = descriptorFactory.CreateRuntimeCompatible(typeof(IJitFallbackContract));
        var proxy = new GeneratedContractProxyFactory(descriptorFactory)
            .Create<IJitFallbackContract>(new RecordingInvoker());

        Assert.Single(descriptor.Methods);
        Assert.False(GeneratedContractRegistry.IsComplete(typeof(IJitFallbackContract)));
        Assert.DoesNotContain("AnyProtocol.Generated", proxy.GetType().FullName);
    }

    [Fact]
    public async Task Message_dispatcher_preserves_jit_fallback_for_unregistered_contract()
    {
        using var serializer = new TextJsonMessageSerializer();
        var service = new JitFallbackService();
        var dispatcher = new MessageDispatcher(
            new SingleServiceResolverFactory(typeof(JitFallbackService), service),
            serializer);
        var method = Assert.Single(
            new ContractDescriptorFactory().Create(typeof(IJitFallbackContract)).Methods);
        var registration = new ServerRegistration(
            typeof(IJitFallbackContract),
            typeof(JitFallbackService),
            "unused");
        var envelope = new TransportEnvelope(
            new MessageHeaders
            {
                [HeaderNames.MessageId] = "jit-fallback",
                [HeaderNames.MessageType] = MessageType.Request.ToString()
            },
            ReadOnlyMemory<byte>.Empty);

        await dispatcher.DispatchAsync(
            registration,
            method,
            envelope,
            new CapturingTransport());

        Assert.Equal(1, service.Calls);
        Assert.False(
            GeneratedServerDispatchRegistry.IsRegistered(
                typeof(IJitFallbackContract),
                nameof(IJitFallbackContract.NotifyAsync)));
    }

    private interface IInvalidContract
    {
        Task<ProxyResponse> InvalidAsync(ProxyRequest first, ProxyRequest second);
    }

    private interface IInvalidPartitionedContract
    {
        ValueTask PublishAsync(InvalidPartitionedRequest request);
    }

    private interface IUnregisteredContract
    {
        ValueTask ExecuteAsync();
    }

    private interface ILegacyGeneratedContract
    {
        ValueTask ExecuteAsync();
    }

    private sealed class JitFallbackService : IJitFallbackContract
    {
        public int Calls { get; private set; }

        public ValueTask NotifyAsync()
        {
            Calls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleServiceResolverFactory(Type serviceType, object service)
        : IDependencyResolverFactory
    {
        public IDependencyResolver CreateResolver() => new Resolver(serviceType, service);

        public IAsyncDependencyScope CreateAsyncScope()
            => new Scope(CreateResolver());

        private sealed class Resolver(Type serviceType, object service) : IDependencyResolver
        {
            public TService? Resolve<TService>() where TService : class
                => Resolve(typeof(TService)) as TService;

            public object? Resolve(Type requestedType)
                => requestedType == serviceType ? service : null;

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

    private sealed class CapturingTransport : IMessagingProtocol
    {
        public TransportEnvelope? Envelope { get; private set; }

        public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            Envelope = envelope;
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

    private sealed class RecordingInvoker : IClientInvoker
    {
        public List<Invocation> Calls { get; } = [];

        public ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
            ContractMethodDescriptor method,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Invocation(method, request, cancellationToken));
            var response = new ProxyResponse(((ProxyRequest)(object)request!).Value);
            return ValueTask.FromResult((TResponse)(object)response);
        }

        public ValueTask SendAsync<TRequest>(
            ContractMethodDescriptor method,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new Invocation(method, request, cancellationToken));
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TItem> StreamAsync<TRequest, TItem>(
            ContractMethodDescriptor method,
            TRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(new Invocation(method, request, cancellationToken));
            await Task.Yield();
            yield return (TItem)(object)new ProxyResponse(((ProxyRequest)(object)request!).Value);
        }
    }

    private sealed class RecordingOrderInvoker : IClientInvoker
    {
        public ValueTask<TResponse> RequestAsync<TRequest, TResponse>(
            ContractMethodDescriptor method,
            TRequest request,
            CancellationToken cancellationToken = default)
        {
            var order = Assert.IsType<PlaceOrderRequest>(request);
            return ValueTask.FromResult(
                (TResponse)(object)new PlaceOrderResponse(order.OrderId, 0));
        }

        public ValueTask SendAsync<TRequest>(
            ContractMethodDescriptor method,
            TRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TItem> StreamAsync<TRequest, TItem>(
            ContractMethodDescriptor method,
            TRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed record Invocation(
        ContractMethodDescriptor Method,
        object? Request,
        CancellationToken CancellationToken);
}
