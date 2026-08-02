using System.Text;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqEngineTests(RabbitMqFixture fixture)
    : IClassFixture<RabbitMqFixture>
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [SkippableFact]
    public async Task Request_reply_uses_isolated_reply_channel()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions();
        await using var server = new RabbitMqMessagingProtocol(options);
        await using var client = new RabbitMqMessagingProtocol(options);
        await using var responder = await server.SubscribeAsync(
            "orders.lookup",
            async (request, cancellationToken) =>
            {
                var headers = new MessageHeaders
                {
                    [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
                    [HeaderNames.MessageType] = MessageType.Response.ToString()
                };
                await server.SendAsync(
                    request.Headers[HeaderNames.ReplyTo]!,
                    new TransportEnvelope(headers, request.Body),
                    cancellationToken);
            },
            new SubscriptionOptions { ConsumerGroup = $"servers-{Guid.NewGuid():N}" });
        await using var engine = new RequestReplyEngine(client);

        var response = await engine.RequestAsync(
            "orders.lookup",
            new TransportEnvelope(new MessageHeaders(), "order-42"u8.ToArray()),
            Timeout);

        Assert.Equal("order-42", Encoding.UTF8.GetString(response.Body.Span));
    }

    [SkippableFact]
    public async Task Stream_engine_receives_ordered_items_and_completion()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions();
        await using var server = new RabbitMqMessagingProtocol(options);
        await using var client = new RabbitMqMessagingProtocol(options);
        await using var responder = await server.SubscribeAsync(
            "orders.stream",
            async (request, cancellationToken) =>
            {
                for (var sequence = 0; sequence < 2; sequence++)
                {
                    await SendStreamResponseAsync(
                        server,
                        request,
                        MessageType.StreamItem,
                        sequence,
                        Encoding.UTF8.GetBytes(sequence.ToString()),
                        cancellationToken);
                }

                await SendStreamResponseAsync(
                    server,
                    request,
                    MessageType.StreamComplete,
                    2,
                    ReadOnlyMemory<byte>.Empty,
                    cancellationToken);
            });
        await using var engine = new StreamEngine(client);
        var items = new List<string>();

        await foreach (var item in engine.StreamAsync(
                           "orders.stream",
                           new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty),
                           Timeout))
        {
            items.Add(Encoding.UTF8.GetString(item.Body.Span));
        }

        Assert.Equal(["0", "1"], items);
    }

    private static ValueTask SendStreamResponseAsync(
        RabbitMqMessagingProtocol transport,
        TransportEnvelope request,
        MessageType messageType,
        long sequence,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var headers = new MessageHeaders
        {
            [HeaderNames.CorrelationId] = request.Headers[HeaderNames.CorrelationId],
            [HeaderNames.MessageType] = messageType.ToString(),
            [HeaderNames.StreamSequence] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return transport.SendAsync(
            request.Headers[HeaderNames.ReplyTo]!,
            new TransportEnvelope(headers, body),
            cancellationToken);
    }
}
