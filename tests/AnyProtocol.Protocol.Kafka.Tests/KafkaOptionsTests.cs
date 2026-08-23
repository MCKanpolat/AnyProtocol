using Confluent.Kafka;
using AnyProtocol.Protocol.Kafka;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaOptionsTests
{
    [Fact]
    public void Validator_accepts_valid_options_and_applies_client_overrides()
    {
        var options = new KafkaProtocolOptions
        {
            BootstrapServers = "localhost:9092",
            ProducerConfig = new Dictionary<string, string>
            {
                ["compression.type"] = "gzip"
            }
        };

        Assert.Same(options, KafkaProtocolOptionsValidator.Validate(options));

        var config = new ClientConfig();
        KafkaProtocolOptionsValidator.ApplyOverrides(
            config,
            new Dictionary<string, string> { ["client.id"] = "override" });

        Assert.Equal("override", config.ClientId);
    }

    [Fact]
    public void Validator_rejects_missing_or_non_positive_values()
    {
        Assert.Throws<ArgumentNullException>(
            () => KafkaProtocolOptionsValidator.Validate(null!));
        Assert.Throws<ArgumentException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = " " }));
        Assert.Throws<ArgumentException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", ClientId = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", TopicPartitions = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", TopicReplicationFactor = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", ReplyTopicRetention = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", DeadLetterSuffix = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", SubscriptionStartupTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", ConsumerPollInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => KafkaProtocolOptionsValidator.Validate(
                new KafkaProtocolOptions { BootstrapServers = "localhost", ProducerFlushTimeout = TimeSpan.Zero }));
    }

    [Fact]
    public void Link_builder_rejects_duplicate_protocol_registration()
    {
        using var serializer = new AnyProtocol.Serializer.TextJson.TextJsonMessageSerializer();
        var builder = new AnyProtocol.Configuration.LinkBuilder()
            .UseSerializer(serializer);
        var options = new KafkaProtocolOptions { BootstrapServers = "localhost:9092" };

        builder.AddKafka(options);

        Assert.Throws<InvalidOperationException>(() => builder.AddKafka(options));
    }
}
