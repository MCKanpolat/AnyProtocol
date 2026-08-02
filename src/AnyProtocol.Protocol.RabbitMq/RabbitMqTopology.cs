using System.Text;

namespace AnyProtocol.Protocol.RabbitMq;

internal sealed class RabbitMqTopology
{
    private const int MaxEntityNameBytes = 255;
    private readonly RabbitMqProtocolOptions _options;

    public RabbitMqTopology(RabbitMqProtocolOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateName(options.ExchangeName, nameof(options.ExchangeName));
    }

    public RabbitMqSubscriptionTopology ForSubscription(
        string channel,
        string? consumerGroup)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        if (consumerGroup is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(consumerGroup);
        }

        var routingKey = Join(_options.RoutingKeyPrefix, channel);
        ValidateName(routingKey, nameof(channel));

        string? queueName = null;
        var durable = false;
        var exclusive = true;
        var autoDelete = true;
        if (consumerGroup is not null)
        {
            queueName = Join(_options.ExchangeName, routingKey, consumerGroup);
            ValidateName(queueName, nameof(consumerGroup));
            durable = true;
            exclusive = false;
            autoDelete = false;
        }

        return new RabbitMqSubscriptionTopology(
            _options.ExchangeName,
            routingKey,
            queueName,
            durable,
            exclusive,
            autoDelete,
            DeadLetterExchange,
            DeadLetterQueue);
    }

    public string DeadLetterExchange
    {
        get
        {
            var name = _options.ExchangeName + _options.DeadLetterSuffix;
            ValidateName(name, nameof(_options.DeadLetterSuffix));
            return name;
        }
    }

    public string DeadLetterQueue => DeadLetterExchange;

    public string RoutingKey(string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var routingKey = Join(_options.RoutingKeyPrefix, channel);
        ValidateName(routingKey, nameof(channel));
        return routingKey;
    }

    private static string Join(params string?[] parts)
        => string.Join('.', parts.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static void ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (Encoding.UTF8.GetByteCount(value) > MaxEntityNameBytes)
        {
            throw new ArgumentException(
                "RabbitMQ entity names cannot exceed 255 UTF-8 bytes.",
                parameterName);
        }
    }
}

internal sealed record RabbitMqSubscriptionTopology(
    string ExchangeName,
    string RoutingKey,
    string? QueueName,
    bool Durable,
    bool Exclusive,
    bool AutoDelete,
    string DeadLetterExchange,
    string DeadLetterQueue);
