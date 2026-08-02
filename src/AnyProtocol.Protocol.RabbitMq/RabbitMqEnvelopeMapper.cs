using System.Globalization;
using System.Text;
using AnyProtocol.Abstraction;
using RabbitMQ.Client;

namespace AnyProtocol.Protocol.RabbitMq;

internal static class RabbitMqEnvelopeMapper
{
    internal const string DeliveryAttemptHeader = "cl-delivery-attempt";

    private static readonly HashSet<string> NativeHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.MessageId,
        HeaderNames.CorrelationId,
        HeaderNames.ReplyTo,
        HeaderNames.ContentType,
        HeaderNames.SentAt
    };

    public static BasicProperties ToProperties(
        TransportEnvelope envelope,
        int deliveryAttempt)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (deliveryAttempt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryAttempt));
        }

        var properties = new BasicProperties
        {
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = envelope.Headers[HeaderNames.MessageId],
            CorrelationId = envelope.Headers[HeaderNames.CorrelationId],
            ReplyTo = envelope.Headers[HeaderNames.ReplyTo],
            ContentType = envelope.Headers[HeaderNames.ContentType],
            Headers = new Dictionary<string, object?>
            {
                [DeliveryAttemptHeader] = deliveryAttempt
            }
        };

        if (DateTimeOffset.TryParse(
                envelope.Headers[HeaderNames.SentAt],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var sentAt))
        {
            properties.Timestamp = new AmqpTimestamp(sentAt.ToUnixTimeSeconds());
        }

        foreach (var header in envelope.Headers)
        {
            if (!NativeHeaders.Contains(header.Key) &&
                !string.Equals(
                    header.Key,
                    DeliveryAttemptHeader,
                    StringComparison.OrdinalIgnoreCase))
            {
                properties.Headers[header.Key] = Encoding.UTF8.GetBytes(header.Value);
            }
        }

        return properties;
    }

    public static TransportEnvelope FromDelivery(
        IReadOnlyBasicProperties properties,
        ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var headers = new MessageHeaders();
        SetIfPresent(headers, HeaderNames.MessageId, properties.MessageId);
        SetIfPresent(headers, HeaderNames.CorrelationId, properties.CorrelationId);
        SetIfPresent(headers, HeaderNames.ReplyTo, properties.ReplyTo);
        SetIfPresent(headers, HeaderNames.ContentType, properties.ContentType);
        if (properties.IsTimestampPresent())
        {
            headers[HeaderNames.SentAt] =
                DateTimeOffset.FromUnixTimeSeconds(properties.Timestamp.UnixTime)
                    .ToString("O", CultureInfo.InvariantCulture);
        }

        if (properties.Headers is not null)
        {
            foreach (var header in properties.Headers)
            {
                headers[header.Key] = ConvertHeaderValue(header.Key, header.Value);
            }
        }

        return new TransportEnvelope(headers, body.ToArray());
    }

    private static void SetIfPresent(MessageHeaders headers, string key, string? value)
    {
        if (value is not null)
        {
            headers[key] = value;
        }
    }

    private static string ConvertHeaderValue(string key, object? value)
        => value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            sbyte or byte or short or ushort or int or uint or long or ulong =>
                Convert.ToString(value, CultureInfo.InvariantCulture)!,
            null => string.Empty,
            _ => throw new InvalidDataException(
                $"RabbitMQ header '{key}' has an unsupported value type.")
        };
}
