using System.Diagnostics;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.InMemory;
using AnyProtocol.Protocol.Kafka;
using AnyProtocol.Protocol.RabbitMq;

namespace AnyProtocol.LoadTests;

public interface ILoadTransportFactory
{
    string Name { get; }
    ValueTask<IMessagingProtocol> CreateAsync(CancellationToken cancellationToken = default);
}

public static class TransportFactories
{
    public static ILoadTransportFactory Create(LoadTestOptions options)
    {
        options.Validate();
        var prefix = $"perf-{Guid.NewGuid():N}";
        return options.Transport switch
        {
            "inmemory" => new DelegateFactory("inmemory", static () => new InMemoryMessagingProtocol()),
            "rabbitmq" => new DelegateFactory(
                "rabbitmq",
                () => new RabbitMqMessagingProtocol(new RabbitMqProtocolOptions
                {
                    ConnectionUri = options.RabbitMqUri!,
                    ExchangeName = $"anyprotocol.{prefix}",
                    RoutingKeyPrefix = prefix
                })),
            "kafka" => new DelegateFactory(
                "kafka",
                () => new KafkaMessagingProtocol(new KafkaProtocolOptions
                {
                    BootstrapServers = options.KafkaBootstrapServers!,
                    TopicPrefix = prefix,
                    AutoCreateTopics = true
                })),
            _ => throw new UnreachableException()
        };
    }

    private sealed class DelegateFactory(string name, Func<IMessagingProtocol> create)
        : ILoadTransportFactory
    {
        public string Name => name;

        public ValueTask<IMessagingProtocol> CreateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(create());
        }
    }
}
