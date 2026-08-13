using AnyProtocol.Tests.Shared;
using Testcontainers.Redis;
using Xunit;

namespace AnyProtocol.Storage.Redis.Tests;

public sealed class RedisFixture : IAsyncLifetime
{
    private RedisContainer? _container;

    public string? UnavailableReason { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("ANYPROTOCOL_REDIS_CONNECTION");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured;
            return;
        }

        try
        {
            _container = new RedisBuilder("redis:7.4-alpine").Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            if (BrokerTestMode.RequireBrokerTests())
            {
                throw new InvalidOperationException(
                    "Redis Testcontainer startup failed while infrastructure tests are required.",
                    exception);
            }

            UnavailableReason = $"Redis Testcontainer is unavailable: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void RequireRedis() => Skip.If(UnavailableReason is not null, UnavailableReason);
}
