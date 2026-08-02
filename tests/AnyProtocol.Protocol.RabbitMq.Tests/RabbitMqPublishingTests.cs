using System.Text;
using AnyProtocol.Abstraction;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace AnyProtocol.Protocol.RabbitMq.Tests;

public sealed class RabbitMqPublishingTests(RabbitMqFixture fixture)
    : IClassFixture<RabbitMqFixture>
{
    [SkippableFact]
    public async Task Confirmed_publish_delivers_persistent_message_to_bound_queue()
    {
        fixture.RequireRabbitMq();
        var options = fixture.CreateOptions();
        const string channelName = "orders.created";
        await using var inspectorConnection = await CreateConnectionAsync();
        await using var inspectorChannel = await inspectorConnection.CreateChannelAsync();
        await inspectorChannel.ExchangeDeclareAsync(
            options.ExchangeName,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false);
        var queue = await inspectorChannel.QueueDeclareAsync(
            queue: string.Empty,
            durable: false,
            exclusive: true,
            autoDelete: true);
        await inspectorChannel.QueueBindAsync(
            queue.QueueName,
            options.ExchangeName,
            channelName);
        await using var transport = new RabbitMqMessagingProtocol(options);

        await transport.SendAsync(
            channelName,
            new TransportEnvelope(
                new MessageHeaders { [HeaderNames.MessageId] = "message-1" },
                Encoding.UTF8.GetBytes("payload")));

        BasicGetResult? result = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (result is null)
        {
            result = await inspectorChannel.BasicGetAsync(queue.QueueName, autoAck: true, timeout.Token);
            if (result is null)
            {
                await Task.Delay(25, timeout.Token);
            }
        }

        Assert.Equal(DeliveryModes.Persistent, result.BasicProperties.DeliveryMode);
        Assert.Equal("message-1", result.BasicProperties.MessageId);
        Assert.Equal("payload", Encoding.UTF8.GetString(result.Body.Span));
    }

    [SkippableFact]
    public async Task Mandatory_unroutable_publish_fails_without_exposing_credentials()
    {
        fixture.RequireRabbitMq();
        await using var transport = fixture.CreateTransport();

        var exception = await Assert.ThrowsAnyAsync<PublishException>(
            async () => await transport.SendAsync(
                "unbound.channel",
                new TransportEnvelope(new MessageHeaders(), ReadOnlyMemory<byte>.Empty)));

        Assert.DoesNotContain("guest:guest", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IConnection> CreateConnectionAsync()
    {
        var factory = new ConnectionFactory { Uri = fixture.ConnectionUri };
        return await factory.CreateConnectionAsync();
    }
}
