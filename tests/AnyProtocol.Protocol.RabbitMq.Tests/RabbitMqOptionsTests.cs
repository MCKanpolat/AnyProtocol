using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Protocol.RabbitMq;
using AnyProtocol.Serializer.TextJson;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqOptionsTests
{
    [Fact]
    public void Built_in_protocol_key_is_normalized()
        => Assert.Equal("rabbitmq", ProtocolKey.RabbitMq.Value);

    [Fact]
    public void Options_reject_non_positive_delivery_attempts()
    {
        var options = ValidOptions() with { MaxDeliveryAttempts = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RabbitMqMessagingProtocol(options));
    }

    [Fact]
    public void Options_reject_invalid_connection_and_timing_values()
    {
        var valid = ValidOptions();

        Assert.Throws<ArgumentException>(() => new RabbitMqMessagingProtocol(
            valid with { ConnectionUri = new Uri("http://localhost") }));
        Assert.Throws<ArgumentException>(() => new RabbitMqMessagingProtocol(
            valid with { ClientProvidedName = " " }));
        Assert.Throws<ArgumentException>(() => new RabbitMqMessagingProtocol(
            valid with { ExchangeName = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqMessagingProtocol(
            valid with { PrefetchCount = 0 }));
        Assert.Throws<ArgumentNullException>(() => new RabbitMqMessagingProtocol(
            valid with { ShouldRetryHandlerException = null! }));
        Assert.Throws<ArgumentException>(() => new RabbitMqMessagingProtocol(
            valid with { DeadLetterSuffix = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqMessagingProtocol(
            valid with { ConfirmTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqMessagingProtocol(
            valid with { ReadinessTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqMessagingProtocol(
            valid with { NetworkRecoveryInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RabbitMqMessagingProtocol(
            valid with { ShutdownTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Builder_registers_rabbitmq_under_requested_key()
    {
        using var serializer = new TextJsonMessageSerializer();
        var builder = new LinkBuilder()
            .UseSerializer(serializer)
            .AddRabbitMq(ValidOptions(), ProtocolKey.RabbitMq);

        var exception = Assert.Throws<InvalidOperationException>(
            () => builder.AddRabbitMq(ValidOptions(), ProtocolKey.RabbitMq));

        Assert.Contains("rabbitmq", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RabbitMqProtocolOptions ValidOptions()
        => new()
        {
            ConnectionUri = new Uri("amqp://guest:guest@localhost:5672/")
        };
}
