using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Services;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace AnyProtocol;

/// <summary>
/// Provides the event publisher implementation used by AnyProtocol applications.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EventPublisher<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TEvent>
    : IEventPublisher<TEvent>
    where TEvent : class
{
    private readonly ISendTransport _transport;
    private readonly IMessageSerializer _serializer;
    private readonly string _channel;
    private readonly string _transportName;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly OutboundOperationExecutor _executor;
    private readonly MessageFilterDelegate _pipeline;
    private readonly LargePayloadOffloader _payloadOffloader;
    private static readonly PropertyInfo? PartitionKeyProperty = GetPartitionKeyProperty();

    /// <summary>
    /// Performs the event publisher operation.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="transportName">The transport name.</param>
    /// <param name="executor">The outbound operation executor.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="payloadOffloader">The optional outbound payload processor.</param>
    /// <returns>The result of the event publisher operation.</returns>
    public EventPublisher(
        ISendTransport transport,
        IMessageSerializer serializer,
        string channel,
        string transportName = "unknown",
        OutboundOperationExecutor? executor = null,
        IMessageEnvelopeFactory? envelopeFactory = null,
        LargePayloadOffloader? payloadOffloader = null)
    {
        _transport = transport;
        _serializer = serializer;
        _channel = channel;
        _transportName = transportName;
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
        _payloadOffloader = payloadOffloader ?? new LargePayloadOffloader(
            null,
            new LargePayloadStoreRegistry([]));
        _pipeline = _executor.CreatePipeline(SendAsync);
    }

    /// <summary>
    /// Publishes a message to all subscribers of its configured channel.
    /// </summary>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask PublishAsync(
        TEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var headers = _envelopeFactory.CreateOutboundHeaders(
            MessageType.Event,
            _channel,
            typeof(TEvent).FullName ?? typeof(TEvent).Name);
        if (PartitionKeyProperty is not null)
        {
            var partitionKey = Convert.ToString(
                PartitionKeyProperty.GetValue(@event),
                CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(partitionKey))
            {
                throw new InvalidOperationException(
                    $"Partition key '{typeof(TEvent).FullName}.{PartitionKeyProperty.Name}' " +
                    "must not be null or empty.");
            }

            headers[HeaderNames.PartitionKey] = partitionKey;
        }

        var body = await _serializer.SerializeAsync(@event, cancellationToken).ConfigureAwait(false);
        var context = new MessageContext(
            headers,
            body,
            _channel,
            MessageType.Event,
            MessageDirection.Outbound,
            cancellationToken);
        context.Items[DiagnosticContext.TransportNameKey] = _transportName;
        await _executor.ExecuteAsync(context, _pipeline).ConfigureAwait(false);
    }

    private async ValueTask SendAsync(IMessageContext context)
    {
        var envelope = await _payloadOffloader.OffloadAsync(
                new TransportEnvelope(context.Headers, context.Body),
                context.CancellationToken)
            .ConfigureAwait(false);
        await _transport.SendAsync(_channel, envelope, context.CancellationToken)
            .ConfigureAwait(false);
    }

    private static PropertyInfo? GetPartitionKeyProperty()
    {
        var properties = typeof(TEvent).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.IsDefined(typeof(PartitionKeyAttribute)))
            .ToArray();
        return properties.Length switch
        {
            0 => null,
            1 when properties[0].CanRead && properties[0].GetIndexParameters().Length == 0 =>
                properties[0],
            1 => throw new InvalidOperationException(
                $"[PartitionKey] property '{properties[0].Name}' must be readable and non-indexed."),
            _ => throw new InvalidOperationException(
                $"Event '{typeof(TEvent).FullName}' contains more than one [PartitionKey] property.")
        };
    }
}
