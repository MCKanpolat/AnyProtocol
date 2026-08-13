using System.Reflection;
using AnyProtocol.Abstraction;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class ArchitecturePlanTests
{
    [Fact]
    public void Message_dispatcher_is_a_composition_facade()
    {
        var fieldTypes = typeof(MessageDispatcher)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static field => field.FieldType)
            .ToArray();

        Assert.Equal(6, fieldTypes.Length);
        Assert.Contains(typeof(RequestMessageDispatcher), fieldTypes);
        Assert.Contains(typeof(StreamMessageDispatcher), fieldTypes);
        Assert.Contains(typeof(EventMessageDispatcher), fieldTypes);
        Assert.Contains(typeof(FaultMessageSender), fieldTypes);
        Assert.Contains(typeof(LargePayloadMaterializer), fieldTypes);
        Assert.Contains(typeof(LargePayloadOffloader), fieldTypes);
    }

    [Fact]
    public void Dispatch_components_have_one_way_dependencies()
    {
        Type[] dispatchers =
        [
            typeof(RequestMessageDispatcher),
            typeof(StreamMessageDispatcher),
            typeof(EventMessageDispatcher)
        ];

        foreach (var dispatcher in dispatchers)
        {
            var dependencies = dispatcher
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(static field => field.FieldType)
                .ToArray();

            Assert.DoesNotContain(typeof(MessageDispatcher), dependencies);
            Assert.DoesNotContain(
                dependencies,
                dependency => dispatchers.Contains(dependency) && dependency != dispatcher);
        }

        var streamDependencies = typeof(StreamMessageDispatcher)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static field => field.FieldType);
        Assert.Contains(typeof(DeadlineCancellationFactory), streamDependencies);
    }

    [Fact]
    public void Handler_invoker_does_not_own_transport_sending()
    {
        var dependencies = typeof(HandlerInvoker)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(static field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(ReplyMessageSender), dependencies);
        Assert.DoesNotContain(typeof(FaultMessageSender), dependencies);
        Assert.DoesNotContain(
            dependencies,
            dependency => typeof(IMessagingProtocol).IsAssignableFrom(dependency));
    }

    [Fact]
    public void Envelope_factory_uses_the_fixed_id_and_utc_clock_policy()
    {
        var factory = new DefaultMessageEnvelopeFactory(
            new FixedMessageIdGenerator("message-1"),
            new FixedDateTimeProvider(new DateTimeOffset(2026, 8, 10, 1, 2, 3, TimeSpan.Zero)));

        var headers = factory.CreateOutboundHeaders(
            MessageType.Request,
            "orders",
            "Orders.Contracts.IOrders",
            "GetAsync",
            correlationId: "correlation-1",
            replyTo: "reply-channel");

        Assert.Equal("message-1", headers[HeaderNames.MessageId]);
        Assert.Equal("correlation-1", headers[HeaderNames.CorrelationId]);
        Assert.Equal("reply-channel", headers[HeaderNames.ReplyTo]);
        Assert.Equal("2026-08-10T01:02:03.0000000+00:00", headers[HeaderNames.SentAt]);
        Assert.Equal(MessageType.Request.ToString(), headers[HeaderNames.MessageType]);
    }

    [Fact]
    public void Envelope_factory_rejects_an_empty_custom_id()
    {
        var factory = new DefaultMessageEnvelopeFactory(
            new FixedMessageIdGenerator(" "),
            new FixedDateTimeProvider(DateTimeOffset.UtcNow));

        Assert.Throws<InvalidOperationException>(
            () => factory.CreateOutboundHeaders(MessageType.Event, "orders"));
    }

    [Fact]
    public void Envelope_factory_normalizes_custom_clocks_to_utc()
    {
        var factory = new DefaultMessageEnvelopeFactory(
            new FixedMessageIdGenerator("message-1"),
            new FixedDateTimeProvider(
                new DateTimeOffset(2026, 8, 10, 4, 2, 3, TimeSpan.FromHours(3))));

        var headers = factory.CreateOutboundHeaders(MessageType.Event, "orders");

        Assert.Equal("2026-08-10T01:02:03.0000000+00:00", headers[HeaderNames.SentAt]);
        Assert.Equal(TimeSpan.Zero, factory.GetUtcNow().Offset);
    }

    [Fact]
    public void Envelope_factory_replaces_blank_source_message_ids()
    {
        var factory = new DefaultMessageEnvelopeFactory(
            new FixedMessageIdGenerator("message-1"),
            new FixedDateTimeProvider(DateTimeOffset.UnixEpoch));

        var headers = factory.CreateOutboundHeaders(
            MessageType.Event,
            "orders",
            source: new MessageHeaders { [HeaderNames.MessageId] = " " });

        Assert.Equal("message-1", headers[HeaderNames.MessageId]);
    }

    [Fact]
    public void Dependency_injection_preserves_pre_registered_metadata_policies()
    {
        var idGenerator = new FixedMessageIdGenerator("custom-id");
        var clock = new FixedDateTimeProvider(DateTimeOffset.UnixEpoch);
        var services = new ServiceCollection();
        services.AddSingleton<IMessageIdGenerator>(idGenerator);
        services.AddSingleton<IDateTimeProvider>(clock);
        services.AddAnyProtocol(
            link => link
                .UseSerializer(new TextJsonMessageSerializer())
                .AddTransport("memory", new InMemoryMessagingProtocol()));

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IMessageEnvelopeFactory>();

        Assert.Same(idGenerator, provider.GetRequiredService<IMessageIdGenerator>());
        Assert.Same(clock, provider.GetRequiredService<IDateTimeProvider>());
        Assert.Equal(
            "custom-id",
            factory.CreateOutboundHeaders(MessageType.Event, "orders")[HeaderNames.MessageId]);
    }

    [Fact]
    public async Task Request_reply_engine_applies_metadata_policy_to_native_transports()
    {
        var transport = new CapturingRequestReplyTransport();
        var factory = new DefaultMessageEnvelopeFactory(
            new FixedMessageIdGenerator("message-1"),
            new FixedDateTimeProvider(DateTimeOffset.UnixEpoch));
        await using var engine = new RequestReplyEngine(transport, factory);

        await engine.RequestAsync(
            "orders",
            new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
            TimeSpan.FromSeconds(1));

        Assert.NotNull(transport.Request);
        Assert.Equal("message-1", transport.Request.Headers[HeaderNames.MessageId]);
        Assert.Equal("message-1", transport.Request.Headers[HeaderNames.CorrelationId]);
        Assert.Equal("orders", transport.Request.Headers[HeaderNames.Channel]);
        Assert.Equal("1970-01-01T00:00:00.0000000+00:00", transport.Request.Headers[HeaderNames.SentAt]);
    }

    [Fact]
    public async Task Admission_rejects_new_work_after_the_drain_linearization_point()
    {
        var admission = new RequestAdmissionCoordinator();
        admission.StartAccepting();
        var active = admission.TryEnter();

        Assert.NotNull(active);
        admission.BeginDrain();
        Assert.Null(admission.TryEnter());
        Assert.False(await admission.WaitForIdleAsync(TimeSpan.Zero));

        active!.Dispose();

        Assert.True(await admission.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, admission.ActiveCount);
    }

    [Fact]
    public async Task Admission_remains_race_safe_under_concurrent_entries_and_drain()
    {
        var admission = new RequestAdmissionCoordinator();
        admission.StartAccepting();
        using var start = new ManualResetEventSlim();
        var workers = Enumerable.Range(0, 4_000)
            .Select(
                _ => Task.Run(
                    () =>
                    {
                        start.Wait();
                        admission.TryEnter()?.Dispose();
                    }))
            .ToArray();

        start.Set();
        admission.BeginDrain();
        await Task.WhenAll(workers);

        Assert.All(Enumerable.Range(0, 4_000), _ => Assert.Null(admission.TryEnter()));
        Assert.True(await admission.WaitForIdleAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, admission.ActiveCount);
    }

    private sealed class FixedMessageIdGenerator(string value) : IMessageIdGenerator
    {
        public string Generate() => value;
    }

    private sealed class FixedDateTimeProvider(DateTimeOffset value) : IDateTimeProvider
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CapturingRequestReplyTransport : IRequestReplyTransport
    {
        public TransportEnvelope? Request { get; private set; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask<TransportEnvelope> RequestAsync(
            string channel,
            TransportEnvelope request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
