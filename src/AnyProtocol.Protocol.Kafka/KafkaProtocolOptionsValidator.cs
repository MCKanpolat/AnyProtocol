using Confluent.Kafka;

namespace AnyProtocol.Protocol.Kafka;

internal static class KafkaProtocolOptionsValidator
{
    public static KafkaProtocolOptions Validate(KafkaProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BootstrapServers);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClientId);
        if (options.TopicPartitions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TopicPartitions));
        }

        if (options.TopicReplicationFactor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.TopicReplicationFactor));
        }

        if (options.ReplyTopicRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ReplyTopicRetention));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(options.DeadLetterSuffix);
        if (options.SubscriptionStartupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.SubscriptionStartupTimeout));
        }

        if (options.ConsumerPollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ConsumerPollInterval));
        }

        if (options.ProducerFlushTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ProducerFlushTimeout));
        }

        return options;
    }

    public static void ApplyOverrides(
        ClientConfig config,
        IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var pair in overrides)
        {
            config.Set(pair.Key, pair.Value);
        }
    }
}
