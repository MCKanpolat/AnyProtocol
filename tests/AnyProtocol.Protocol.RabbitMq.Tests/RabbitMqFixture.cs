using Testcontainers.RabbitMq;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string? UnavailableReason { get; private set; }

    public Uri ConnectionUri => new(
        _container?.GetConnectionString() ??
        throw new InvalidOperationException("The RabbitMQ container is not running."));

    public async Task InitializeAsync()
    {
        try
        {
            _container = new RabbitMqBuilder("rabbitmq:4.1-management").Build();
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            UnavailableReason = $"RabbitMQ Testcontainer is unavailable: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void RequireRabbitMq() => Skip.If(UnavailableReason is not null, UnavailableReason);

    public RabbitMqMessagingProtocol CreateTransport(string? prefix = null)
    {
        RequireRabbitMq();
        return new RabbitMqMessagingProtocol(CreateOptions(prefix));
    }

    public RabbitMqProtocolOptions CreateOptions(string? prefix = null)
    {
        RequireRabbitMq();
        return new RabbitMqProtocolOptions
        {
            ConnectionUri = ConnectionUri,
            ExchangeName = $"anyprotocol.{prefix ?? $"test-{Guid.NewGuid():N}"}",
            ConfirmTimeout = TimeSpan.FromSeconds(20),
            ShutdownTimeout = TimeSpan.FromSeconds(20)
        };
    }
}
