using System.Text;
using AnyProtocol.Abstraction;
using Confluent.Kafka;

namespace AnyProtocol.Protocol.Kafka;

internal static class KafkaEnvelopeMapper
{
    public static TransportEnvelope CreateEnvelope(ConsumeResult<string, byte[]> result)
    {
        var headers = new MessageHeaders();
        foreach (var header in result.Message.Headers)
        {
            var value = header.GetValueBytes();
            if (value is not null)
            {
                headers[header.Key] = Encoding.UTF8.GetString(value);
            }
        }

        return new TransportEnvelope(headers, result.Message.Value ?? []);
    }
}
