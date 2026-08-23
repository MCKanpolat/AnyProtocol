using System.Text;
using AnyProtocol.Protocol.Kafka;
using Confluent.Kafka;

namespace AnyProtocol.Protocol.Kafka.Tests;

public sealed class KafkaEnvelopeMapperTests
{
    [Fact]
    public void CreateEnvelope_maps_utf8_headers_and_preserves_the_body()
    {
        var body = new byte[] { 1, 2, 255 };
        var result = new ConsumeResult<string, byte[]>
        {
            Message = new Message<string, byte[]>
            {
                Value = body,
                Headers = new Headers
                {
                    new Header("x-name", Encoding.UTF8.GetBytes("çağrı")),
                    new Header("x-null", null!)
                }
            }
        };

        var envelope = KafkaEnvelopeMapper.CreateEnvelope(result);
        Assert.Equal("çağrı", envelope.Headers["X-NAME"]);
        Assert.Null(envelope.Headers["x-null"]);
        Assert.Equal(new byte[] { 1, 2, 255 }, envelope.Body.ToArray());
    }

    [Fact]
    public void CreateEnvelope_uses_an_empty_body_for_a_null_kafka_value()
    {
        var result = new ConsumeResult<string, byte[]>
        {
            Message = new Message<string, byte[]>
            {
                Value = null,
                Headers = new Headers()
            }
        };

        var envelope = KafkaEnvelopeMapper.CreateEnvelope(result);

        Assert.Empty(envelope.Body.ToArray());
        Assert.Empty(envelope.Headers);
    }
}
