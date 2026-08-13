using Testcontainers.Kafka;
using AnyProtocol.Tests.Shared;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaFixture : IAsyncLifetime
{
    private KafkaContainer? _container;

    public string? UnavailableReason { get; private set; }

    public string BootstrapServers =>
        _container?.GetBootstrapAddress() ??
        throw new InvalidOperationException("The Kafka container is not running.");

    public async Task InitializeAsync()
    {
        var requireBrokerTests = BrokerTestMode.RequireBrokerTests();

        try
        {
            _container = new KafkaBuilder("confluentinc/cp-kafka:7.6.0").Build();
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            if (requireBrokerTests)
            {
                throw new InvalidOperationException(
                    "Kafka Testcontainer startup failed while broker tests are required. " +
                    $"Set {BrokerTestMode.RequireBrokerTestsEnvironmentVariable}=false for optional local broker tests.",
                    exception);
            }

            UnavailableReason = $"Kafka Testcontainer is unavailable: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public void RequireKafka()
    {
        Skip.If(UnavailableReason is not null, UnavailableReason);
    }

    public KafkaMessagingProtocol CreateTransport(string? prefix = null)
    {
        RequireKafka();
        return new KafkaMessagingProtocol(
            new KafkaProtocolOptions
            {
                BootstrapServers = BootstrapServers,
                TopicPrefix = prefix ?? $"test-{Guid.NewGuid():N}",
                SubscriptionStartupTimeout = TimeSpan.FromSeconds(20)
            });
    }
}
