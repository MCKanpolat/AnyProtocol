using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class HardeningTests
{
    [Fact]
    public void Server_registration_accepts_multiple_typed_protocols()
    {
        var configuration = new LinkBuilder()
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport(
                ProtocolKey.InMemory,
                new InMemoryMessagingProtocol())
            .AddTransport(
                ProtocolKey.Create("secondary-memory"),
                new InMemoryMessagingProtocol())
            .AddServer<IHardeningService, HardeningService>(
                options => options.UseProtocols(
                    ProtocolKey.InMemory,
                    ProtocolKey.Create("secondary-memory")))
            .Build();

        Assert.Equal(
            [ProtocolKey.InMemory, ProtocolKey.Create("secondary-memory")],
            configuration.ServerRegistrations.Single().Protocols);
    }

    [Fact]
    public void Configuration_rejects_duplicate_server_protocols()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(ProtocolKey.InMemory, new InMemoryMessagingProtocol())
                .AddServer<IHardeningService, HardeningService>(
                    options => options.UseProtocols(
                        ProtocolKey.InMemory,
                        ProtocolKey.Create("INMEMORY")))
                .Build());

        Assert.Contains("more than once", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_when_any_server_protocol_lacks_capabilities()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(ProtocolKey.InMemory, new InMemoryMessagingProtocol())
                .AddTransport(
                    ProtocolKey.Create("limited"),
                    new TestTransport(TransportCapabilities.NativeHeaders))
                .AddServer<IHardeningService, HardeningService>(
                    options => options.UseProtocols(
                        ProtocolKey.InMemory,
                        ProtocolKey.Create("limited")))
                .Build());

        Assert.Contains("limited", exception.Message);
        Assert.Contains("server subscriptions", exception.Message);
    }

    [Fact]
    public void Legacy_capabilities_receive_conservative_semantics()
    {
        var transport = new TestTransport(TransportCapabilities.NativeStreaming);

        Assert.Equal(TransportDeliveryGuarantee.AtMostOnce, transport.Semantics.DeliveryGuarantee);
        Assert.Equal(TransportOrdering.None, transport.Semantics.Ordering);
        Assert.Equal(TransportDurability.Volatile, transport.Semantics.Durability);
        Assert.True(transport.Semantics.SupportsNativeStreaming);
        Assert.True(transport.Semantics.SupportsBackpressure);
    }

    [Fact]
    public void In_memory_transport_declares_explicit_semantics()
    {
        var transport = new InMemoryMessagingProtocol();

        Assert.Equal(TransportDeliveryGuarantee.AtMostOnce, transport.Semantics.DeliveryGuarantee);
        Assert.Equal(TransportOrdering.PerChannel, transport.Semantics.Ordering);
        Assert.Equal(TransportDurability.Volatile, transport.Semantics.Durability);
        Assert.True(transport.Semantics.SupportsBackpressure);
        Assert.True(transport.Semantics.SupportsCancellation);
        Assert.False(transport.Semantics.SupportsPartitioning);
    }

    [Fact]
    public void Generated_descriptor_preserves_idempotency_metadata()
    {
        var descriptor = new ContractDescriptorFactory().Create<IHardeningService>();

        Assert.True(
            descriptor.Methods.Single(method => method.MethodName == nameof(IHardeningService.GetAsync))
                .IsIdempotent);
        Assert.False(
            descriptor.Methods.Single(
                    method => method.MethodName == nameof(IHardeningService.GetOtherAsync))
                .IsIdempotent);
    }

    [Fact]
    public void Configuration_rejects_events_without_publish_subscribe()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("limited", new TestTransport(TransportCapabilities.NativeHeaders))
                .AddEventHandler<TestEvent, TestEventHandler>(
                    options => options.UseTransport("limited"))
                .Build());

        Assert.Contains("event publish/subscribe", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_consumer_groups_without_competing_consumers()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(
                    "limited",
                    new TestTransport(TransportCapabilities.PublishSubscribe))
                .AddEventHandler<TestEvent, TestEventHandler>(
                    options => options.UseTransport("limited").ConsumerGroup("workers"))
                .Build());

        Assert.Contains("event consumer groups", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_emulated_requests_without_publish_subscribe()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("limited", new TestTransport(TransportCapabilities.NativeHeaders))
                .AddClient<IHardeningService>(options => options.UseTransport("limited"))
                .Build());

        Assert.Contains("request/reply emulation", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_streams_without_native_or_emulated_streaming()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(
                    "limited",
                    new TestTransport(TransportCapabilities.NativeRequestReply))
                .AddClient<IStreamHardeningService>(
                    options => options.UseTransport("limited"))
                .Build());

        Assert.Contains("contains streaming methods", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_partition_keys_on_non_partitioned_transport()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(
                    "limited",
                    new TestTransport(TransportCapabilities.PublishSubscribe))
                .AddClient<IPartitionedHardeningService>(
                    options => options.UseTransport("limited"))
                .Build());

        Assert.Contains("does not support partitioning", exception.Message);
    }

    [Fact]
    public void Configuration_rejects_unsupported_ordering()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport(
                    "limited",
                    new TestTransport(
                        TransportCapabilities.NativeRequestReply,
                        new TransportSemantics
                        {
                            DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
                            Ordering = TransportOrdering.None,
                            Durability = TransportDurability.Volatile
                        }))
                .AddClient<IHardeningService>(
                    options => options
                        .UseTransport("limited")
                        .RequireOrdering(TransportOrdering.PerChannel))
                .Build());

        Assert.Contains("requires PerChannel ordering", exception.Message);
    }

    [Fact]
    public async Task Bus_parallel_start_stop_and_dispose_are_idempotent()
    {
        var transport = new TestTransport(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers);
        await using var provider = CreateProvider(transport);
        var bus = provider.GetRequiredService<AnyProtocolBus>();

        await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => bus.StartAsync().AsTask()));

        Assert.Equal(2, transport.SubscribeCount);
        Assert.Equal(AnyProtocolBusState.Started, bus.State);

        await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => bus.StopAsync().AsTask()));

        Assert.Equal(2, transport.SubscriptionDisposeCount);
        Assert.Equal(AnyProtocolBusState.Stopped, bus.State);

        await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => bus.DisposeAsync().AsTask()));

        Assert.Equal(1, transport.TransportDisposeCount);
        Assert.Equal(AnyProtocolBusState.Disposed, bus.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => bus.StartAsync().AsTask());
    }

    [Fact]
    public async Task Bus_subscribes_one_logical_server_on_every_selected_protocol()
    {
        var primary = new TestTransport(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers);
        var secondary = new TestTransport(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers);
        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .AddTransport(ProtocolKey.Create("primary"), primary)
            .AddTransport(ProtocolKey.Create("secondary"), secondary)
            .AddServer<IHardeningService, HardeningService>(options => options.UseProtocols(
                ProtocolKey.Create("primary"),
                ProtocolKey.Create("secondary"))));
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();

        Assert.Equal(2, primary.SubscribeCount);
        Assert.Equal(2, secondary.SubscribeCount);
    }

    [Fact]
    public async Task Bus_rolls_back_partial_startup_and_can_restart()
    {
        var transport = new TestTransport(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers)
        {
            FailSubscriptionNumber = 2
        };
        await using var provider = CreateProvider(transport);
        var bus = provider.GetRequiredService<AnyProtocolBus>();

        await Assert.ThrowsAsync<IOException>(() => bus.StartAsync().AsTask());

        Assert.Equal(1, transport.SubscriptionDisposeCount);
        Assert.Equal(AnyProtocolBusState.Stopped, bus.State);

        await bus.StartAsync();

        Assert.Equal(AnyProtocolBusState.Started, bus.State);
        Assert.Equal(4, transport.SubscribeCount);
    }

    [Fact]
    public async Task Bus_retries_failed_subscription_cleanup_before_becoming_stopped()
    {
        var transport = new TestTransport(
            TransportCapabilities.PublishSubscribe |
            TransportCapabilities.CompetingConsumers)
        {
            FailFirstSubscriptionDisposeOnce = true
        };
        await using var provider = CreateProvider(transport);
        var bus = provider.GetRequiredService<AnyProtocolBus>();
        await bus.StartAsync();

        await Assert.ThrowsAsync<IOException>(() => bus.StopAsync().AsTask());

        Assert.Equal(AnyProtocolBusState.Stopping, bus.State);

        await bus.StopAsync();

        Assert.Equal(AnyProtocolBusState.Stopped, bus.State);
        Assert.Equal(2, transport.SubscriptionDisposeCount);
    }

    [Fact]
    public async Task Bus_stop_cancels_an_in_flight_handler()
    {
        BlockingHardeningService.Reset();
        var transport = new InMemoryMessagingProtocol();
        await using var provider = CreateProvider(transport, useBlockingService: true);
        var bus = provider.GetRequiredService<AnyProtocolBus>();
        await bus.StartAsync();
        var client = provider.GetRequiredService<IHardeningService>();
        var request = client.GetAsync(new HardeningRequest("wait"), CancellationToken.None).AsTask();
        await BlockingHardeningService.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await bus.StopAsync();

        await BlockingHardeningService.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(request.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Request_reply_engine_starts_once_and_ignores_duplicate_responses()
    {
        var transport = new ReplyingTransport(duplicateResponses: true);
        await using var engine = new RequestReplyEngine(transport);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 64)
                .Select(
                    _ => engine.RequestAsync(
                            "requests",
                            EmptyEnvelope(),
                            TimeSpan.FromSeconds(2))
                        .AsTask()));

        Assert.Equal(64, responses.Length);
        Assert.Equal(1, transport.SubscribeCount);
    }

    [Fact]
    public async Task Request_reply_engine_disposal_completes_pending_calls()
    {
        var transport = new PendingTransport();
        var engine = new RequestReplyEngine(transport);
        var pending = engine.RequestAsync(
                "requests",
                EmptyEnvelope(),
                Timeout.InfiniteTimeSpan)
            .AsTask();
        await transport.SendObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => engine.DisposeAsync().AsTask()));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
    }

    [Fact]
    public async Task Request_reply_engine_retries_failed_subscription_cleanup()
    {
        var transport = new ReplyingTransport(
            duplicateResponses: false,
            failFirstDisposal: true);
        var engine = new RequestReplyEngine(transport);
        _ = await engine.RequestAsync(
            "requests",
            EmptyEnvelope(),
            TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<IOException>(() => engine.DisposeAsync().AsTask());
        await engine.DisposeAsync();

        Assert.Equal(2, transport.SubscriptionDisposeAttempts);
    }

    [Fact]
    public async Task Request_reply_engine_disposal_cancels_concurrent_startup()
    {
        var transport = new BlockingSubscribeTransport();
        var engine = new RequestReplyEngine(transport);
        var request = engine.RequestAsync(
                "requests",
                EmptyEnvelope(),
                Timeout.InfiniteTimeSpan)
            .AsTask();
        await transport.SubscribeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = engine.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Stream_engine_disposal_completes_pending_streams()
    {
        var transport = new PendingTransport();
        var engine = new StreamEngine(transport);
        await using var enumerator = engine.StreamAsync(
                "stream",
                EmptyEnvelope(),
                Timeout.InfiniteTimeSpan)
            .GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await transport.SendObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Task.WhenAll(
            Enumerable.Range(0, 16).Select(_ => engine.DisposeAsync().AsTask()));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
    }

    [Fact]
    public async Task Stream_engine_disposal_cancels_concurrent_startup()
    {
        var transport = new BlockingSubscribeTransport();
        var engine = new StreamEngine(transport);
        await using var enumerator = engine.StreamAsync(
                "stream",
                EmptyEnvelope(),
                Timeout.InfiniteTimeSpan)
            .GetAsyncEnumerator();
        var pending = enumerator.MoveNextAsync().AsTask();
        await transport.SubscribeObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var disposal = engine.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Retry_filter_retries_only_idempotent_operations()
    {
        var filter = new RetryFilter(
            new RetryOptions
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                JitterFactor = 0
            });
        var idempotentContext = CreateRetryContext(nameof(IRetryContract.SafeAsync));
        var safeAttempts = 0;

        await filter.InvokeAsync(
            idempotentContext,
            _ =>
            {
                safeAttempts++;
                return safeAttempts < 3
                    ? ValueTask.FromException(new IOException("transient"))
                    : ValueTask.CompletedTask;
            });

        var unsafeContext = CreateRetryContext(nameof(IRetryContract.UnsafeAsync));
        var unsafeAttempts = 0;
        await Assert.ThrowsAsync<IOException>(
            () => filter.InvokeAsync(
                    unsafeContext,
                    _ =>
                    {
                        unsafeAttempts++;
                        return ValueTask.FromException(new IOException("transient"));
                    })
                .AsTask());

        Assert.Equal(3, safeAttempts);
        Assert.Equal(1, unsafeAttempts);
    }

    [Fact]
    public async Task Retry_filter_honors_retryable_faults_and_total_time()
    {
        var retryableFilter = new RetryFilter(
            new RetryOptions
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.Zero,
                MaxDelay = TimeSpan.Zero,
                JitterFactor = 0
            });
        var context = CreateRetryContext(nameof(IRetryContract.SafeAsync));
        var attempts = 0;

        await retryableFilter.InvokeAsync(
            context,
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromException(
                        new AnyProtocolFaultException(
                            new FaultMessage("busy", "Try again.", Retryable: true)))
                    : ValueTask.CompletedTask;
            });

        Assert.Equal(2, attempts);

        var nonRetryableAttempts = 0;
        await Assert.ThrowsAsync<AnyProtocolFaultException>(
            () => retryableFilter.InvokeAsync(
                    CreateRetryContext(nameof(IRetryContract.SafeAsync)),
                    _ =>
                    {
                        nonRetryableAttempts++;
                        return ValueTask.FromException(
                            new AnyProtocolFaultException(
                                new FaultMessage("invalid", "Do not retry.")));
                    })
                .AsTask());
        Assert.Equal(1, nonRetryableAttempts);

        var timedFilter = new RetryFilter(
            new RetryOptions
            {
                MaxAttempts = 10,
                InitialDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
                MaxTotalTime = TimeSpan.FromMilliseconds(20)
            });
        await Assert.ThrowsAsync<TimeoutException>(
            () => timedFilter.InvokeAsync(
                    CreateRetryContext(nameof(IRetryContract.SafeAsync)),
                    _ => ValueTask.FromException(new IOException("transient")))
                .AsTask());
    }

    [Fact]
    public async Task Error_handler_is_invoked_without_replacing_original_fault()
    {
        RecordingErrorHandler.Reset();
        var services = new ServiceCollection();
        services.AddSingleton<IErrorHandler, RecordingErrorHandler>();
        services.AddAnyProtocol(
            link => link
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("memory", new InMemoryMessagingProtocol())
                .AddClient<IFailingHardeningService>(
                    options => options.UseTransport("memory"))
                .AddServer<IFailingHardeningService, FailingHardeningService>(
                    options => options.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IAnyProtocolBus>().StartAsync();

        var exception = await Assert.ThrowsAsync<AnyProtocolFaultException>(
            () => provider.GetRequiredService<IFailingHardeningService>()
                .FailAsync(new HardeningRequest("original"))
                .AsTask());

        var reported = await RecordingErrorHandler.Reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("handler_failed", exception.Fault.Code);
        Assert.Contains("original", exception.Message);
        Assert.IsType<InvalidOperationException>(reported);
    }

    private static ServiceProvider CreateProvider(
        IMessagingProtocol transport,
        bool useBlockingService = false)
    {
        var services = new ServiceCollection();
        services.AddAnyProtocol(
            link =>
            {
                link.UseSerializer(new TextJsonMessageSerializer())
                    .AddTransport("test", transport)
                    .AddClient<IHardeningService>(options => options.UseTransport("test"));
                if (useBlockingService)
                {
                    link.AddServer<IHardeningService, BlockingHardeningService>(
                        options => options.UseTransport("test"));
                }
                else
                {
                    link.AddServer<IHardeningService, HardeningService>(
                        options => options.UseTransport("test"));
                }
            });
        return services.BuildServiceProvider();
    }

    private static TransportEnvelope EmptyEnvelope()
        => new(new MessageHeaders(), ReadOnlyMemory<byte>.Empty);

    private static MessageContext CreateRetryContext(string methodName)
    {
        var method = new ContractDescriptorFactory()
            .Create<IRetryContract>()
            .Methods
            .Single(candidate => candidate.MethodName == methodName);
        return new MessageContext(
            new MessageHeaders(),
            ReadOnlyMemory<byte>.Empty,
            method.Channel,
            MessageType.Request,
            MessageDirection.Outbound)
        {
            Method = method
        };
    }

    public sealed record HardeningRequest(string Value);

    public sealed record HardeningResponse(string Value);

    public sealed record TestEvent(string Value);

    public sealed record PartitionedHardeningRequest(
        [property: PartitionKey] string Partition,
        string Value);

    public interface IHardeningService
    {
        [Channel("hardening.one")]
        [Idempotent]
        ValueTask<HardeningResponse> GetAsync(
            HardeningRequest request,
            CancellationToken cancellationToken);

        [Channel("hardening.two")]
        ValueTask<HardeningResponse> GetOtherAsync(HardeningRequest request);
    }

    public interface IPartitionedHardeningService
    {
        ValueTask PublishAsync(PartitionedHardeningRequest request);
    }

    public interface IStreamHardeningService
    {
        IAsyncEnumerable<HardeningResponse> StreamAsync(HardeningRequest request);
    }

    public interface IRetryContract
    {
        [Idempotent]
        ValueTask<HardeningResponse> SafeAsync(HardeningRequest request);

        ValueTask<HardeningResponse> UnsafeAsync(HardeningRequest request);
    }

    public interface IFailingHardeningService
    {
        ValueTask<HardeningResponse> FailAsync(HardeningRequest request);
    }

    public sealed class TestEventHandler : IEventConsumer<TestEvent>
    {
        public ValueTask ConsumeAsync(TestEvent e) => ValueTask.CompletedTask;
    }

    public sealed class HardeningService : IHardeningService
    {
        public ValueTask<HardeningResponse> GetAsync(
            HardeningRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(new HardeningResponse(request.Value));

        public ValueTask<HardeningResponse> GetOtherAsync(HardeningRequest request)
            => ValueTask.FromResult(new HardeningResponse(request.Value));
    }

    public sealed class BlockingHardeningService : IHardeningService
    {
        public static TaskCompletionSource Started { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource Cancelled { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async ValueTask<HardeningResponse> GetAsync(
            HardeningRequest request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new HardeningResponse(request.Value);
        }

        public ValueTask<HardeningResponse> GetOtherAsync(HardeningRequest request)
            => ValueTask.FromResult(new HardeningResponse(request.Value));
    }

    public sealed class FailingHardeningService : IFailingHardeningService
    {
        public ValueTask<HardeningResponse> FailAsync(HardeningRequest request)
            => throw new InvalidOperationException($"Original failure: {request.Value}");
    }

    public sealed class RecordingErrorHandler : IErrorHandler
    {
        public static TaskCompletionSource<Exception> Reported { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
            => Reported = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask HandleAsync(IMessageContext context)
        {
            Reported.TrySetResult(context.Exception!);
            throw new ApplicationException("Error handler failure must not replace the original.");
        }
    }

    private sealed class TestTransport : IMessagingProtocol
    {
        private readonly TransportSemantics? _semantics;
        private int _subscribeCount;
        private int _subscriptionDisposeCount;
        private int _transportDisposeCount;
        private int _disposeFailureInjected;

        public TestTransport(
            TransportCapabilities capabilities,
            TransportSemantics? semantics = null)
        {
            Capabilities = capabilities;
            _semantics = semantics;
        }

        public TransportCapabilities Capabilities { get; }

        public TransportSemantics Semantics =>
            _semantics ?? TransportSemantics.FromCapabilities(Capabilities);

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public int SubscriptionDisposeCount => Volatile.Read(ref _subscriptionDisposeCount);

        public int TransportDisposeCount => Volatile.Read(ref _transportDisposeCount);

        public int? FailSubscriptionNumber { get; init; }

        public bool FailFirstSubscriptionDisposeOnce { get; init; }

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var count = Interlocked.Increment(ref _subscribeCount);
            if (count == FailSubscriptionNumber)
            {
                throw new IOException("Injected subscription failure.");
            }

            var subscription = new TestSubscription(
                () => Interlocked.Increment(ref _subscriptionDisposeCount),
                () => FailFirstSubscriptionDisposeOnce &&
                      count == 1 &&
                      Interlocked.Exchange(ref _disposeFailureInjected, 1) == 0
                    ? new IOException("Injected subscription disposal failure.")
                    : null);
            return ValueTask.FromResult<IAsyncDisposable>(subscription);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _transportDisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReplyingTransport(
        bool duplicateResponses,
        bool failFirstDisposal = false) : IMessagingProtocol
    {
        private Func<TransportEnvelope, CancellationToken, ValueTask>? _handler;
        private int _subscribeCount;
        private int _subscriptionDisposeAttempts;

        public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

        public int SubscribeCount => Volatile.Read(ref _subscribeCount);

        public int SubscriptionDisposeAttempts =>
            Volatile.Read(ref _subscriptionDisposeAttempts);

        public async ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            var response = new TransportEnvelope(
                new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = envelope.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                },
                ReadOnlyMemory<byte>.Empty);
            await _handler!(response, cancellationToken);
            if (duplicateResponses)
            {
                await _handler(response, cancellationToken);
            }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _subscribeCount);
            _handler = handler;
            return ValueTask.FromResult<IAsyncDisposable>(
                new TestSubscription(
                    getDisposalFailure: () =>
                        Interlocked.Increment(ref _subscriptionDisposeAttempts) == 1 &&
                        failFirstDisposal
                            ? new IOException("Injected subscription disposal failure.")
                            : null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PendingTransport : IMessagingProtocol
    {
        public TaskCompletionSource SendObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            SendObserved.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IAsyncDisposable>(new TestSubscription());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSubscribeTransport : IMessagingProtocol
    {
        public TaskCompletionSource SubscribeObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TransportCapabilities Capabilities => TransportCapabilities.PublishSubscribe;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public async ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            SubscribeObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new TestSubscription();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TestSubscription(
        Action? onDispose = null,
        Func<Exception?>? getDisposalFailure = null) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ValueTask.CompletedTask;
            }

            var failure = getDisposalFailure?.Invoke();
            if (failure is not null)
            {
                return ValueTask.FromException(failure);
            }

            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose?.Invoke();
            }

            return ValueTask.CompletedTask;
        }
    }
}
