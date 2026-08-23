using AnyProtocol.Storage.Abstraction;
using AnyProtocol.Storage.Redis;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AnyProtocol.Storage.Redis.Tests;

public sealed class RedisOptionsAndValidationTests
{
    [Fact]
    public async Task Redis_service_registration_validates_inputs_and_registers_named_stores()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RedisPayloadStoreServiceCollectionExtensions.AddAnyProtocolRedisPayloadStore(
                null!,
                "payloads",
                _ => { }));

        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddAnyProtocolRedisPayloadStore(" ", _ => { }));
        Assert.Throws<ArgumentNullException>(() => services.AddAnyProtocolRedisPayloadStore("payloads", null!));
        Assert.Throws<ArgumentNullException>(() => services.AddAnyProtocolRedisPayloadStore("payloads", _ => { }));

        services.AddAnyProtocolRedisPayloadStore(
            "payloads",
            options =>
            {
                options.ConnectionString = "localhost:6379";
                options.KeyPrefix = "anyprotocol:payloads";
            });
        services.AddAnyProtocolRedisDeduplicationStore(
            "inbox",
            options =>
            {
                options.ConnectionString = "localhost:6379";
                options.KeyPrefix = "anyprotocol:inbox";
            });

        await using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<ILargePayloadStore>());
        Assert.Single(provider.GetServices<IMessageDeduplicationStore>());
    }

    [Fact]
    public async Task Redis_stores_validate_constructor_and_operation_arguments_without_connecting()
    {
        Assert.Throws<ArgumentException>(() => new RedisLargePayloadStore(
            " ",
            new RedisPayloadStoreOptions
            {
                ConnectionString = "localhost:6379",
                KeyPrefix = "payloads"
            }));
        Assert.Throws<ArgumentException>(() => new RedisMessageDeduplicationStore(
            "inbox",
            new RedisMessageDeduplicationOptions
            {
                ConnectionString = " ",
                KeyPrefix = "inbox"
            }));

        await using var payloadStore = new RedisLargePayloadStore(
            "payloads",
            new RedisPayloadStoreOptions
            {
                ConnectionString = "localhost:6379",
                KeyPrefix = "payloads"
            });
        await using var inboxStore = new RedisMessageDeduplicationStore(
            "inbox",
            new RedisMessageDeduplicationOptions
            {
                ConnectionString = "localhost:6379",
                KeyPrefix = "inbox"
            });

        await Assert.ThrowsAsync<ArgumentException>(() => payloadStore.StoreAsync(
                "message",
                new byte[] { 1 },
                new PayloadWriteOptions(TimeSpan.Zero, 1, "digest"))
            .AsTask());
        await Assert.ThrowsAsync<LargePayloadException>(() => payloadStore.LoadAsync(
                new StoredPayloadReference("other", "key", 0, "digest", DateTimeOffset.UtcNow))
            .AsTask());
        await Assert.ThrowsAsync<LargePayloadException>(() => payloadStore.DeleteAsync(
                new StoredPayloadReference("other", "key", 0, "digest", DateTimeOffset.UtcNow))
            .AsTask());

        await Assert.ThrowsAsync<ArgumentException>(() => inboxStore.TryAcquireAsync(
                " ",
                TimeSpan.FromSeconds(1))
            .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inboxStore.TryAcquireAsync(
                "message",
                TimeSpan.Zero)
            .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => inboxStore.CompleteAsync(
                new MessageDeduplicationLease("message", "token"),
                TimeSpan.Zero)
            .AsTask());
    }
}
