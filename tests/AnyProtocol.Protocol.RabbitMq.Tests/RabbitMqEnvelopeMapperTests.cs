using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.RabbitMq;
using RabbitMQ.Client;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqEnvelopeMapperTests
{
    [Fact]
    public void Invalid_inputs_are_rejected_before_mapping()
    {
        Assert.Throws<ArgumentNullException>(
            () => RabbitMqEnvelopeMapper.ToProperties(null!, 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RabbitMqEnvelopeMapper.ToProperties(
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                0));
        Assert.Throws<ArgumentNullException>(
            () => RabbitMqEnvelopeMapper.FromDelivery(null!, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Known_and_custom_headers_round_trip_through_amqp_properties()
    {
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var envelope = new TransportEnvelope(
            new MessageHeaders
            {
                [HeaderNames.MessageId] = "message-1",
                [HeaderNames.CorrelationId] = "correlation-1",
                [HeaderNames.ReplyTo] = "reply.orders",
                [HeaderNames.ContentType] = "application/json",
                [HeaderNames.SentAt] = sentAt.ToString("O"),
                ["x-custom"] = "custom-value"
            },
            Encoding.UTF8.GetBytes("payload"));

        var properties = RabbitMqEnvelopeMapper.ToProperties(envelope, deliveryAttempt: 2);
        var roundTrip = RabbitMqEnvelopeMapper.FromDelivery(properties, envelope.Body);

        Assert.Equal(DeliveryModes.Persistent, properties.DeliveryMode);
        Assert.Equal("message-1", properties.MessageId);
        Assert.Equal("correlation-1", properties.CorrelationId);
        Assert.Equal("reply.orders", properties.ReplyTo);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(sentAt.ToUnixTimeSeconds(), properties.Timestamp.UnixTime);
        Assert.Equal("message-1", roundTrip.Headers[HeaderNames.MessageId]);
        Assert.Equal("correlation-1", roundTrip.Headers[HeaderNames.CorrelationId]);
        Assert.Equal("reply.orders", roundTrip.Headers[HeaderNames.ReplyTo]);
        Assert.Equal("application/json", roundTrip.Headers[HeaderNames.ContentType]);
        Assert.Equal(sentAt.ToString("O"), roundTrip.Headers[HeaderNames.SentAt]);
        Assert.Equal("custom-value", roundTrip.Headers["X-CUSTOM"]);
        Assert.Equal("2", roundTrip.Headers[RabbitMqEnvelopeMapper.DeliveryAttemptHeader]);
        Assert.Equal("payload", Encoding.UTF8.GetString(roundTrip.Body.Span));
    }

    [Fact]
    public void Delivery_body_is_copied_before_consumer_memory_can_change()
    {
        var body = new byte[] { 1, 2, 3 };

        var envelope = RabbitMqEnvelopeMapper.FromDelivery(
            RabbitMqEnvelopeMapper.ToProperties(
                new TransportEnvelope(new MessageHeaders(), body),
                deliveryAttempt: 1),
            body);
        body[0] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, envelope.Body.ToArray());
    }

    [Fact]
    public void Header_value_variants_are_normalized_and_invalid_values_are_rejected()
    {
        var properties = new BasicProperties
        {
            Headers = new Dictionary<string, object?>
            {
                ["byte-array"] = Encoding.UTF8.GetBytes("bytes"),
                ["memory"] = (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("memory"),
                ["text"] = "text",
                ["number"] = 42,
                ["null"] = null
            }
        };

        var envelope = RabbitMqEnvelopeMapper.FromDelivery(properties, ReadOnlyMemory<byte>.Empty);

        Assert.Equal("bytes", envelope.Headers["byte-array"]);
        Assert.Equal("memory", envelope.Headers["memory"]);
        Assert.Equal("text", envelope.Headers["text"]);
        Assert.Equal("42", envelope.Headers["number"]);
        Assert.Equal(string.Empty, envelope.Headers["null"]);

        properties.Headers["unsupported"] = new object();
        Assert.Throws<InvalidDataException>(
            () => RabbitMqEnvelopeMapper.FromDelivery(properties, ReadOnlyMemory<byte>.Empty));
    }

    [Fact]
    public void Invalid_sent_at_is_not_promoted_to_a_native_timestamp()
    {
        var properties = RabbitMqEnvelopeMapper.ToProperties(
            new TransportEnvelope(
                new MessageHeaders { [HeaderNames.SentAt] = "not-a-timestamp" },
                ReadOnlyMemory<byte>.Empty),
            1);

        Assert.False(properties.IsTimestampPresent());
    }
}
