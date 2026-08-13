using System.Security.Cryptography;
using AnyProtocol.Storage.Abstraction;
using Xunit;

namespace AnyProtocol.Storage.Redis.Tests;

public sealed class RedisLargePayloadStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [SkippableFact]
    public async Task Redis_provider_conforms_to_idempotency_ttl_and_reader_contracts()
    {
        fixture.RequireRedis();
        await using var store = new RedisLargePayloadStore(
            "payloads",
            new RedisPayloadStoreOptions
            {
                ConnectionString = fixture.ConnectionString,
                KeyPrefix = $"anyprotocol:test:{Guid.NewGuid():N}"
            });
        var body = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var options = Options(body, TimeSpan.FromSeconds(5));

        var first = await store.StoreAsync("message-1", body, options);
        var duplicate = await store.StoreAsync("message-1", body, options);
        var readers = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => store.LoadAsync(first).AsTask()));

        Assert.Equal(first, duplicate);
        Assert.All(readers, value => Assert.Equal(body, value.ToArray()));

        var conflictBody = new byte[] { 4, 5, 6 };
        var conflict = await Assert.ThrowsAsync<LargePayloadException>(
            () => store.StoreAsync(
                    "message-1",
                    conflictBody,
                    Options(conflictBody, TimeSpan.FromSeconds(5)))
                .AsTask());
        Assert.Equal(LargePayloadFailureCodes.IdConflict, conflict.Code);

        await store.DeleteAsync(first);
        var missing = await Assert.ThrowsAsync<LargePayloadException>(
            () => store.LoadAsync(first).AsTask());
        Assert.Equal(LargePayloadFailureCodes.NotFound, missing.Code);

        var expiring = await store.StoreAsync(
            "message-2",
            body,
            Options(body, TimeSpan.FromMilliseconds(100)));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var expired = await Assert.ThrowsAsync<LargePayloadException>(
            () => store.LoadAsync(expiring).AsTask());
        Assert.Equal(LargePayloadFailureCodes.NotFound, expired.Code);
    }

    private static PayloadWriteOptions Options(byte[] body, TimeSpan ttl)
        => new(
            ttl,
            body.Length,
            Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant());
}
