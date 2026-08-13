using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace AnyProtocol.Protocol.RabbitMq;

internal sealed class RabbitMqSubscription : ITransportSubscription
{
    private readonly IChannel _channel;
    private readonly RabbitMqMessagingProtocol _owner;
    private readonly string _sourceChannel;
    private readonly Func<TransportEnvelope, CancellationToken, ValueTask> _handler;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _acknowledgementGate = new(1, 1);
    private readonly List<string> _consumerTags = [];
    private readonly object _acceptingGate = new();
    private bool _accepting = true;
    private int _disposed;

    internal event Action? Disposed;

    private RabbitMqSubscription(
        RabbitMqMessagingProtocol owner,
        IChannel channel,
        string sourceChannel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler)
    {
        _owner = owner;
        _channel = channel;
        _sourceChannel = sourceChannel;
        _handler = handler;
    }

    public static async ValueTask<RabbitMqSubscription> CreateAsync(
        RabbitMqMessagingProtocol owner,
        IConnection connection,
        RabbitMqProtocolOptions options,
        RabbitMqSubscriptionTopology topology,
        string sourceChannel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: false,
                publisherConfirmationTrackingEnabled: false,
                consumerDispatchConcurrency: checked((ushort)maxConcurrency)),
            cancellationToken);
        try
        {
            await channel.ExchangeDeclareAsync(
                topology.ExchangeName,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                cancellationToken: cancellationToken);
            if (options.EnableDeadLetter)
            {
                await channel.ExchangeDeclareAsync(
                    topology.DeadLetterExchange,
                    ExchangeType.Topic,
                    durable: true,
                    autoDelete: false,
                    cancellationToken: cancellationToken);
                await channel.QueueDeclareAsync(
                    topology.DeadLetterQueue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    cancellationToken: cancellationToken);
                await channel.QueueBindAsync(
                    topology.DeadLetterQueue,
                    topology.DeadLetterExchange,
                    "#",
                    cancellationToken: cancellationToken);
            }

            var declaration = await channel.QueueDeclareAsync(
                topology.QueueName ?? string.Empty,
                topology.Durable,
                topology.Exclusive,
                topology.AutoDelete,
                cancellationToken: cancellationToken);
            await channel.QueueBindAsync(
                declaration.QueueName,
                topology.ExchangeName,
                topology.RoutingKey,
                cancellationToken: cancellationToken);
            await channel.BasicQosAsync(
                prefetchSize: 0,
                prefetchCount: options.PrefetchCount,
                global: false,
                cancellationToken);

            var subscription = new RabbitMqSubscription(
                owner,
                channel,
                sourceChannel,
                handler);
            for (var index = 0; index < maxConcurrency; index++)
            {
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += subscription.OnReceivedAsync;
                var consumerTag = await channel.BasicConsumeAsync(
                    declaration.QueueName,
                    autoAck: false,
                    consumer,
                    cancellationToken);
                subscription._consumerTags.Add(consumerTag);
            }

            return subscription;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_acceptingGate)
        {
            _accepting = false;
        }

        await _lifetime.CancelAsync();
        if (_channel.IsOpen)
        {
            foreach (var consumerTag in _consumerTags)
            {
                await _channel.BasicCancelAsync(
                    consumerTag,
                    noWait: false,
                    CancellationToken.None);
            }

            await _channel.CloseAsync(CancellationToken.None);
        }

        await _channel.DisposeAsync();
        _acknowledgementGate.Dispose();
        _lifetime.Dispose();
        Disposed?.Invoke();
    }

    public async ValueTask StopAcceptingAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_acceptingGate)
        {
            _accepting = false;
        }

        foreach (var consumerTag in _consumerTags)
        {
            if (_channel.IsOpen)
            {
                await _channel.BasicCancelAsync(
                        consumerTag,
                        noWait: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task OnReceivedAsync(object sender, BasicDeliverEventArgs delivery)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            delivery.CancellationToken);
        var envelope = RabbitMqEnvelopeMapper.FromDelivery(
            delivery.BasicProperties,
            delivery.Body);
        try
        {
            ValueTask handling;
            lock (_acceptingGate)
            {
                if (!_accepting)
                {
                    handling = ValueTask.FromException(
                        new MessageAdmissionRejectedException());
                }
                else
                {
                    handling = _handler(envelope, cancellation.Token);
                }
            }

            await handling;
            await AcknowledgeAsync(delivery.DeliveryTag, cancellation.Token);
        }
        catch (MessageAdmissionRejectedException)
        {
            await NegativeAcknowledgeIfOpenAsync(delivery.DeliveryTag);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await NegativeAcknowledgeIfOpenAsync(delivery.DeliveryTag);
        }
        catch (Exception exception)
        {
            await HandleFailureAsync(delivery.DeliveryTag, envelope, exception, cancellation.Token);
        }
    }

    private async ValueTask HandleFailureAsync(
        ulong deliveryTag,
        TransportEnvelope envelope,
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var attempt = _owner.GetDeliveryAttempt(envelope);
            var failure = exception;
            bool shouldRetry;
            try
            {
                shouldRetry = _owner.ShouldRetry(exception);
            }
            catch (Exception classifierException)
            {
                shouldRetry = false;
                failure = classifierException;
            }

            if (shouldRetry && attempt < _owner.MaxDeliveryAttempts)
            {
                await _owner.RepublishAsync(
                    _sourceChannel,
                    envelope,
                    attempt + 1,
                    cancellationToken);
                await AcknowledgeAsync(deliveryTag, cancellationToken);
                return;
            }

            if (_owner.DeadLetterEnabled)
            {
                await _owner.SendToDeadLetterAsync(
                    _sourceChannel,
                    envelope,
                    failure,
                    cancellationToken);
                await AcknowledgeAsync(deliveryTag, cancellationToken);
                return;
            }

            await NegativeAcknowledgeIfOpenAsync(deliveryTag, requeue: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await NegativeAcknowledgeIfOpenAsync(deliveryTag);
        }
        catch
        {
            await NegativeAcknowledgeIfOpenAsync(deliveryTag);
        }
    }

    private async ValueTask AcknowledgeAsync(
        ulong deliveryTag,
        CancellationToken cancellationToken)
    {
        await _acknowledgementGate.WaitAsync(cancellationToken);
        try
        {
            await _channel.BasicAckAsync(
                deliveryTag,
                multiple: false,
                cancellationToken);
        }
        finally
        {
            _acknowledgementGate.Release();
        }
    }

    private async ValueTask NegativeAcknowledgeIfOpenAsync(
        ulong deliveryTag,
        bool requeue = true)
    {
        if (!_channel.IsOpen)
        {
            return;
        }

        await _acknowledgementGate.WaitAsync();
        try
        {
            if (_channel.IsOpen)
            {
                await _channel.BasicNackAsync(
                    deliveryTag,
                    multiple: false,
                    requeue,
                    CancellationToken.None);
            }
        }
        finally
        {
            _acknowledgementGate.Release();
        }
    }
}
