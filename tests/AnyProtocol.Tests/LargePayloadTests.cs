using System.Collections.Concurrent;
using System.Security.Cryptography;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using AnyProtocol.Storage.Abstraction;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Tests;

public sealed record LargePayloadRequest(string Value);

public sealed record LargePayloadResponse(string Value);

public sealed record LargePayloadEvent(string Value);

public interface ILargePayloadContract
{
    ValueTask<LargePayloadResponse> EchoAsync(
        LargePayloadRequest request,
        CancellationToken cancellationToken);
}

public sealed class LargePayloadService : ILargePayloadContract
{
    public ValueTask<LargePayloadResponse> EchoAsync(
        LargePayloadRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(new LargePayloadResponse(request.Value));
}

public sealed class LargePayloadEventHandler : IEventConsumer<LargePayloadEvent>
{
    public static TaskCompletionSource<LargePayloadEvent> Received { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask ConsumeAsync(LargePayloadEvent @event)
    {
        Received.TrySetResult(@event);
        return ValueTask.CompletedTask;
    }
}

public sealed class LargePayloadTests
{
    [Fact]
    public async Task Disabled_policy_preserves_the_original_envelope()
    {
        var store = new MemoryPayloadStore("payloads");
        var offloader = new LargePayloadOffloader(
            null,
            new LargePayloadStoreRegistry([store]));
        var envelope = Envelope(new byte[128]);

        var result = await offloader.OffloadAsync(envelope, CancellationToken.None);

        Assert.Same(envelope, result);
        Assert.Equal(0, store.StoreCount);
        Assert.Null(result.Headers[HeaderNames.PayloadMode]);
    }

    [Fact]
    public async Task Oversized_body_round_trips_without_delete_on_read()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-13T10:00:00Z"));
        var store = new MemoryPayloadStore("payloads", clock);
        var stores = new LargePayloadStoreRegistry([store]);
        var policy = Policy();
        var offloader = new LargePayloadOffloader(policy, stores);
        var materializer = new LargePayloadMaterializer(stores, clock);
        var body = Enumerable.Range(0, 128).Select(static value => (byte)value).ToArray();

        var stored = await offloader.OffloadAsync(Envelope(body), CancellationToken.None);
        var first = await materializer.MaterializeAsync(stored, CancellationToken.None);
        var second = await materializer.MaterializeAsync(stored, CancellationToken.None);

        Assert.Empty(stored.Body.ToArray());
        Assert.Equal("stored", stored.Headers[HeaderNames.PayloadMode]);
        Assert.Equal(body, first.Body.ToArray());
        Assert.Equal(body, second.Body.ToArray());
        Assert.Equal(2, store.LoadCount);
        Assert.Equal(0, store.DeleteCount);
    }

    [Fact]
    public async Task Materializer_rejects_expired_and_corrupted_references()
    {
        var clock = new FixedClock(DateTimeOffset.Parse("2026-08-13T10:00:00Z"));
        var store = new MemoryPayloadStore("payloads", clock);
        var stores = new LargePayloadStoreRegistry([store]);
        var offloader = new LargePayloadOffloader(Policy(), stores);
        var materializer = new LargePayloadMaterializer(stores, clock);
        var stored = await offloader.OffloadAsync(Envelope(new byte[128]), CancellationToken.None);

        store.Corrupt(stored.Headers[HeaderNames.PayloadKey]!);
        var integrity = await Assert.ThrowsAsync<LargePayloadException>(
            () => materializer.MaterializeAsync(stored, CancellationToken.None).AsTask());
        Assert.Equal(LargePayloadFailureCodes.IntegrityFailed, integrity.Code);

        clock.UtcNow = clock.UtcNow.AddHours(1);
        var expired = await Assert.ThrowsAsync<LargePayloadException>(
            () => materializer.MaterializeAsync(stored, CancellationToken.None).AsTask());
        Assert.Equal(LargePayloadFailureCodes.NotFound, expired.Code);
    }

    [Fact]
    public async Task Store_is_idempotent_and_rejects_message_id_conflicts()
    {
        var store = new MemoryPayloadStore("payloads");
        var firstBody = new byte[] { 1, 2, 3 };
        var firstDigest = Digest(firstBody);
        var options = new PayloadWriteOptions(
            TimeSpan.FromMinutes(5),
            firstBody.Length,
            firstDigest);

        var first = await store.StoreAsync("message-1", firstBody, options);
        var duplicate = await store.StoreAsync("message-1", firstBody, options);
        var conflict = await Assert.ThrowsAsync<LargePayloadException>(
            () => store.StoreAsync(
                    "message-1",
                    new byte[] { 4, 5, 6 },
                    new PayloadWriteOptions(TimeSpan.FromMinutes(5), 3, Digest([4, 5, 6])))
                .AsTask());

        Assert.Equal(first, duplicate);
        Assert.Equal(LargePayloadFailureCodes.IdConflict, conflict.Code);
    }

    [Fact]
    public void Configuration_requires_explicit_valid_bounds_and_a_registered_store()
    {
        var invalid = Assert.Throws<InvalidOperationException>(
            () => new LinkBuilder()
                .UseSerializer(new TextJsonMessageSerializer())
                .UseLargePayloadOffload(options =>
                {
                    options.StoreName = "payloads";
                    options.MaxInlinePayloadBytes = 32;
                    options.MaxStoredPayloadBytes = 32;
                    options.TimeToLive = TimeSpan.FromMinutes(5);
                })
                .AddTransport("memory", new InMemoryMessagingProtocol())
                .Build());
        Assert.Contains("MaxStoredPayloadBytes", invalid.Message);

        var services = new ServiceCollection();
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .UseLargePayloadOffload(ConfigurePolicy)
            .AddTransport("memory", new InMemoryMessagingProtocol()));
        using var provider = services.BuildServiceProvider();
        var missing = Assert.Throws<LargePayloadException>(
            () => provider.GetRequiredService<LargePayloadOffloader>());
        Assert.Equal(LargePayloadFailureCodes.StoreNotConfigured, missing.Code);
    }

    [Fact]
    public async Task Request_response_and_event_offload_are_transport_independent()
    {
        var store = new MemoryPayloadStore("payloads");
        var transport = new InMemoryMessagingProtocol();
        LargePayloadEventHandler.Received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddSingleton<ILargePayloadStore>(store);
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .UseLargePayloadOffload(ConfigurePolicy)
            .AddTransport("memory", transport)
            .AddClient<ILargePayloadContract>(options => options.UseTransport("memory"))
            .AddServer<ILargePayloadContract, LargePayloadService>(
                options => options.UseTransport("memory"))
            .AddEventHandler<LargePayloadEvent, LargePayloadEventHandler>(
                options => options.UseTransport("memory")));
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IAnyProtocolBus>();
        await bus.StartAsync();
        var largeValue = new string('x', 512);

        var response = await provider.GetRequiredService<ILargePayloadContract>()
            .EchoAsync(new LargePayloadRequest(largeValue), CancellationToken.None);
        await provider.GetRequiredService<IEventPublisher<LargePayloadEvent>>()
            .PublishAsync(new LargePayloadEvent(largeValue));
        var received = await LargePayloadEventHandler.Received.Task
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(largeValue, response.Value);
        Assert.Equal(largeValue, received.Value);
        Assert.True(store.StoreCount >= 3);
        Assert.True(store.LoadCount >= 3);
        Assert.Equal(0, store.DeleteCount);
        await bus.StopAsync();
    }

    [Fact]
    public async Task Storage_write_failure_happens_before_transport_publication()
    {
        var transport = new CapturingRequestTransport();
        var services = new ServiceCollection();
        services.AddSingleton<ILargePayloadStore>(new FailingPayloadStore("payloads"));
        services.AddAnyProtocol(link => link
            .UseSerializer(new TextJsonMessageSerializer())
            .UseLargePayloadOffload(ConfigurePolicy)
            .AddTransport("capture", transport)
            .AddClient<ILargePayloadContract>(options => options.UseTransport("capture")));
        await using var provider = services.BuildServiceProvider();
        var bus = provider.GetRequiredService<IAnyProtocolBus>();
        await bus.StartAsync();

        var exception = await Assert.ThrowsAsync<LargePayloadException>(
            () => provider.GetRequiredService<ILargePayloadContract>()
                .EchoAsync(new LargePayloadRequest(new string('x', 512)), CancellationToken.None)
                .AsTask());

        Assert.Equal(LargePayloadFailureCodes.StoreUnavailable, exception.Code);
        Assert.Equal(0, transport.RequestCount);
        await bus.StopAsync();
    }

    private static LargePayloadOffloadPolicy Policy()
        => new("payloads", 32, 1024, TimeSpan.FromMinutes(5));

    private static void ConfigurePolicy(LargePayloadOffloadOptions options)
    {
        options.StoreName = "payloads";
        options.MaxInlinePayloadBytes = 32;
        options.MaxStoredPayloadBytes = 1024;
        options.TimeToLive = TimeSpan.FromMinutes(5);
    }

    private static TransportEnvelope Envelope(ReadOnlyMemory<byte> body)
        => new(
            new MessageHeaders { [HeaderNames.MessageId] = "message-1" },
            body);

    private static string Digest(ReadOnlySpan<byte> body)
        => Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    private sealed class FixedClock(DateTimeOffset utcNow) : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class MemoryPayloadStore : ILargePayloadStore
    {
        private readonly ConcurrentDictionary<string, Entry> _entries = new();
        private readonly FixedClock _clock;

        public MemoryPayloadStore(string name, FixedClock? clock = null)
        {
            Name = name;
            _clock = clock ?? new FixedClock(TimeProvider.System.GetUtcNow());
        }

        public string Name { get; }

        public int StoreCount { get; private set; }

        public int LoadCount { get; private set; }

        public int DeleteCount { get; private set; }

        public ValueTask<StoredPayloadReference> StoreAsync(
            string messageId,
            ReadOnlyMemory<byte> payload,
            PayloadWriteOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoreCount++;
            var key = $"memory:{messageId}";
            var expiresAt = _clock.GetUtcNow().Add(options.TimeToLive);
            var entry = new Entry(payload.ToArray(), options.Sha256, expiresAt);
            var actual = _entries.GetOrAdd(key, entry);
            if (!string.Equals(actual.Digest, options.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LargePayloadException(
                    LargePayloadFailureCodes.IdConflict,
                    "The message ID already contains different content.");
            }

            return ValueTask.FromResult(
                new StoredPayloadReference(
                    Name,
                    key,
                    actual.Body.Length,
                    actual.Digest,
                    actual.ExpiresAt));
        }

        public ValueTask<ReadOnlyMemory<byte>> LoadAsync(
            StoredPayloadReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            if (!_entries.TryGetValue(reference.Key, out var entry) ||
                entry.ExpiresAt <= _clock.GetUtcNow())
            {
                throw new LargePayloadException(
                    LargePayloadFailureCodes.NotFound,
                    "The payload is missing or expired.");
            }

            return ValueTask.FromResult<ReadOnlyMemory<byte>>(entry.Body);
        }

        public ValueTask DeleteAsync(
            StoredPayloadReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            _entries.TryRemove(reference.Key, out _);
            return ValueTask.CompletedTask;
        }

        public void Corrupt(string key)
        {
            var entry = _entries[key];
            _entries[key] = entry with { Body = new byte[] { 0 } };
        }

        private sealed record Entry(byte[] Body, string Digest, DateTimeOffset ExpiresAt);
    }

    private sealed class FailingPayloadStore(string name) : ILargePayloadStore
    {
        public string Name { get; } = name;

        public ValueTask<StoredPayloadReference> StoreAsync(
            string messageId,
            ReadOnlyMemory<byte> payload,
            PayloadWriteOptions options,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<StoredPayloadReference>(
                new LargePayloadException(
                    LargePayloadFailureCodes.StoreUnavailable,
                    "The test store is unavailable.",
                    retryable: true));

        public ValueTask<ReadOnlyMemory<byte>> LoadAsync(
            StoredPayloadReference reference,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DeleteAsync(
            StoredPayloadReference reference,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CapturingRequestTransport : IRequestReplyTransport
    {
        public int RequestCount { get; private set; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask<TransportEnvelope> RequestAsync(
            string channel,
            TransportEnvelope request,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            return ValueTask.FromResult(
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
