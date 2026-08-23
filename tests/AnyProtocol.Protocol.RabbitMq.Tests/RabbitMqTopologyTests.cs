using AnyProtocol.Protocol.RabbitMq;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqTopologyTests
{
    [Fact]
    public void Constructor_and_subscription_validate_required_names()
    {
        Assert.Throws<ArgumentNullException>(() => new RabbitMqTopology(null!));
        Assert.Throws<ArgumentException>(() => new RabbitMqTopology(
            new RabbitMqProtocolOptions
            {
                ConnectionUri = new Uri("amqp://localhost"),
                ExchangeName = " "
            }));

        var topology = CreateTopology();
        Assert.Throws<ArgumentException>(() => topology.ForSubscription(" ", null));
        Assert.Throws<ArgumentException>(() => topology.ForSubscription("orders", " "));
        Assert.Throws<ArgumentException>(() => topology.RoutingKey(" "));
    }

    [Fact]
    public void Consumer_group_uses_stable_durable_queue()
    {
        var topology = CreateTopology().ForSubscription("orders.created", "billing");

        Assert.Equal("app.orders.created", topology.RoutingKey);
        Assert.Equal("anyprotocol.app.orders.created.billing", topology.QueueName);
        Assert.True(topology.Durable);
        Assert.False(topology.Exclusive);
        Assert.False(topology.AutoDelete);
        Assert.Equal("anyprotocol.dead-letter", topology.DeadLetterExchange);
        Assert.Equal("anyprotocol.dead-letter", topology.DeadLetterQueue);
    }

    [Fact]
    public void Subscription_without_group_requests_server_named_fanout_queue()
    {
        var topology = CreateTopology().ForSubscription("orders.created", null);

        Assert.Null(topology.QueueName);
        Assert.False(topology.Durable);
        Assert.True(topology.Exclusive);
        Assert.True(topology.AutoDelete);
    }

    [Fact]
    public void Names_longer_than_amqp_short_string_are_rejected()
    {
        var channel = new string('é', 128);

        var exception = Assert.Throws<ArgumentException>(
            () => CreateTopology().ForSubscription(channel, "billing"));

        Assert.Contains("255 UTF-8 bytes", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dead_letter_name_is_validated_when_read()
    {
        var topology = new RabbitMqTopology(new RabbitMqProtocolOptions
        {
            ConnectionUri = new Uri("amqp://guest:guest@localhost:5672/"),
            ExchangeName = new string('a', 250),
            DeadLetterSuffix = ".dead-letter"
        });

        Assert.Throws<ArgumentException>(() => _ = topology.DeadLetterExchange);
    }

    private static RabbitMqTopology CreateTopology()
        => new(new RabbitMqProtocolOptions
        {
            ConnectionUri = new Uri("amqp://guest:guest@localhost:5672/"),
            RoutingKeyPrefix = "app"
        });
}
