using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using System.Globalization;
using System.Reflection;

namespace AnyProtocol;

/// <summary>
/// Provides the event publisher implementation used by AnyProtocol applications.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public sealed class EventPublisher<TEvent> : IEventPublisher<TEvent>
    where TEvent : class
{
    private readonly ISendTransport _transport;
    private readonly IMessageSerializer _serializer;
    private readonly string _channel;
    private readonly string _transportName;
    private static readonly PropertyInfo? PartitionKeyProperty = GetPartitionKeyProperty();

    /// <summary>
    /// Performs the event publisher operation.
    /// </summary>
    /// <param name="transport">The transport.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="transportName">The transport name.</param>
    /// <returns>The result of the event publisher operation.</returns>
    public EventPublisher(
        ISendTransport transport,
        IMessageSerializer serializer,
        string channel,
        string transportName = "unknown")
    {
        _transport = transport;
        _serializer = serializer;
        _channel = channel;
        _transportName = transportName;
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
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.Channel] = _channel,
            [HeaderNames.Contract] = typeof(TEvent).FullName ?? typeof(TEvent).Name,
            [HeaderNames.MessageType] = MessageType.Event.ToString(),
            [HeaderNames.ContentType] = "application/x-anyprotocol",
            [HeaderNames.SentAt] = DateTimeOffset.UtcNow.ToString("O")
        };
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
        var pipeline = PipelineBuilder.Build(
            ObservabilityFilters.AddDefaults(null),
            async current =>
                await _transport.SendAsync(
                        _channel,
                        new TransportEnvelope(current.Headers, current.Body),
                        current.CancellationToken)
                    .ConfigureAwait(false));
        await pipeline(context).ConfigureAwait(false);
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
