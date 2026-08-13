using System.Globalization;
using AnyProtocol.Abstraction;

namespace AnyProtocol.Services;

/// <summary>
/// Provides the default deterministic message metadata policy.
/// </summary>
public sealed class DefaultMessageEnvelopeFactory : IMessageEnvelopeFactory
{
    private const string DefaultContentType = "application/x-anyprotocol";
    private readonly IMessageIdGenerator _messageIdGenerator;
    private readonly IDateTimeProvider _dateTimeProvider;

    /// <summary>
    /// Creates the documented default metadata policy for manually constructed clients.
    /// </summary>
    /// <returns>A default envelope factory.</returns>
    public static DefaultMessageEnvelopeFactory CreateDefault()
        => new(new DefaultMessageIdGenerator(), new DefaultDateTimeProvider());

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultMessageEnvelopeFactory"/> class.
    /// </summary>
    public DefaultMessageEnvelopeFactory(
        IMessageIdGenerator messageIdGenerator,
        IDateTimeProvider dateTimeProvider)
    {
        _messageIdGenerator = messageIdGenerator ?? throw new ArgumentNullException(nameof(messageIdGenerator));
        _dateTimeProvider = dateTimeProvider ?? throw new ArgumentNullException(nameof(dateTimeProvider));
    }

    /// <inheritdoc />
    public string CreateMessageId()
    {
        var value = _messageIdGenerator.Generate();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("The configured message id generator returned an empty value.")
            : value;
    }

    /// <inheritdoc />
    public DateTimeOffset GetUtcNow() => _dateTimeProvider.GetUtcNow().ToUniversalTime();

    /// <inheritdoc />
    public IMessageHeaders CreateOutboundHeaders(
        MessageType messageType,
        string channel,
        string? contractName = null,
        string? methodName = null,
        string? contentType = null,
        IMessageHeaders? source = null,
        string? correlationId = null,
        string? replyTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var headers = source is null ? new MessageHeaders() : new MessageHeaders(source);
        if (string.IsNullOrWhiteSpace(headers[HeaderNames.MessageId]))
        {
            headers[HeaderNames.MessageId] = CreateMessageId();
        }
        headers[HeaderNames.Channel] = channel;
        headers[HeaderNames.MessageType] = messageType.ToString();
        headers[HeaderNames.ContentType] = contentType ??
                                           headers[HeaderNames.ContentType] ??
                                           DefaultContentType;
        headers[HeaderNames.SentAt] = GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        if (contractName is not null)
        {
            headers[HeaderNames.Contract] = contractName;
        }

        if (methodName is not null)
        {
            headers[HeaderNames.Method] = methodName;
        }

        if (correlationId is not null)
        {
            headers[HeaderNames.CorrelationId] = correlationId;
        }

        if (replyTo is not null)
        {
            headers[HeaderNames.ReplyTo] = replyTo;
        }

        return headers;
    }

    /// <inheritdoc />
    public IMessageHeaders CreateResponseHeaders(
        IMessageHeaders requestHeaders,
        MessageType messageType)
    {
        ArgumentNullException.ThrowIfNull(requestHeaders);
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = CreateMessageId(),
            [HeaderNames.CorrelationId] = requestHeaders[HeaderNames.CorrelationId] ??
                                          requestHeaders[HeaderNames.MessageId],
            [HeaderNames.MessageType] = messageType.ToString(),
            [HeaderNames.ContentType] = requestHeaders[HeaderNames.ContentType] ?? DefaultContentType,
            [HeaderNames.SentAt] = GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
        };
        return headers;
    }
}
