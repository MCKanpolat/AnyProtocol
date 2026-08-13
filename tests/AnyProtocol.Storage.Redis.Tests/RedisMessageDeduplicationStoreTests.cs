using AnyProtocol.Storage.Abstraction;
using Xunit;

namespace AnyProtocol.Storage.Redis.Tests;

public sealed class RedisMessageDeduplicationStoreTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>
{
    [SkippableFact]
    public async Task Redis_inbox_claims_complete_and_release_atomically()
    {
        fixture.RequireRedis();
        await using var store = CreateStore();

        var leases = await Task.WhenAll(
            Enumerable.Range(0, 32)
                .Select(
                    _ => store.TryAcquireAsync("message-1", TimeSpan.FromSeconds(30))
                        .AsTask()));
        var owner = Assert.Single(leases, static lease => lease is not null)!.Value;

        await store.CompleteAsync(owner, TimeSpan.FromMinutes(1));
        Assert.Null(await store.TryAcquireAsync("message-1", TimeSpan.FromSeconds(30)));

        var retry = await store.TryAcquireAsync("message-2", TimeSpan.FromSeconds(30));
        Assert.NotNull(retry);
        await store.ReleaseAsync(retry.Value);
        Assert.NotNull(await store.TryAcquireAsync("message-2", TimeSpan.FromSeconds(30)));
    }

    private RedisMessageDeduplicationStore CreateStore()
        => new(
            "inbox",
            new RedisMessageDeduplicationOptions
            {
                ConnectionString = fixture.ConnectionString,
                KeyPrefix = $"anyprotocol:inbox-test:{Guid.NewGuid():N}"
            });
}
