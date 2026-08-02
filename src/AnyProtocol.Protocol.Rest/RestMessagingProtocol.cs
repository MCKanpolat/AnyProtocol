using System.Net.Http.Headers;
using System.Text.Json;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol.Protocol.Rest;

/// <summary>
/// Implements rest messaging messaging transport operations.
/// </summary>
public sealed class RestMessagingProtocol : IMessagingProtocol, INativeRequestReplyTransport
{
    private static readonly JsonSerializerOptions ProblemJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _httpClient;
    private readonly IMessageSerializer _serializer;
    private readonly string _routePrefix;

    /// <summary>
    /// Initializes a new instance of the RestMessagingProtocol class.
    /// </summary>
    /// <param name="httpClient">The http client.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="routePrefix">The route prefix.</param>
    public RestMessagingProtocol(
        HttpClient httpClient,
        IMessageSerializer serializer,
        string routePrefix = "/anyprotocol")
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        var normalizedPrefix = routePrefix.Trim('/');
        _routePrefix = normalizedPrefix.Length == 0 ? string.Empty : $"/{normalizedPrefix}";
    }

    /// <summary>
    /// Initializes a new instance of the RestMessagingProtocol class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="clientName">The client name.</param>
    /// <param name="routePrefix">The route prefix.</param>
    public RestMessagingProtocol(
        IHttpClientFactory httpClientFactory,
        IMessageSerializer serializer,
        string clientName,
        string routePrefix = "/anyprotocol")
        : this(
            httpClientFactory?.CreateClient(clientName) ??
            throw new ArgumentNullException(nameof(httpClientFactory)),
            serializer,
            routePrefix)
    {
    }

    /// <summary>
    /// Gets the optional transport capabilities supported by this protocol.
    /// </summary>
    /// <value>The capabilities.</value>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeHeaders |
        TransportCapabilities.NativeRequestReply;

    /// <summary>
    /// Gets the delivery and ordering guarantees provided by this protocol.
    /// </summary>
    /// <value>The semantics.</value>
    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.None,
        Durability = TransportDurability.Volatile,
        SupportsNativeRequestReply = true,
        SupportsBackpressure = true,
        SupportsCancellation = true
    };

    /// <summary>
    /// Sends a transport envelope to the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(channel, envelope);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            var fault = ParseProblem(body, response.ReasonPhrase);
            if (string.Equals(fault.Code, "validation_failed", StringComparison.Ordinal))
            {
                throw new AnyProtocolValidationException(fault);
            }

            throw new AnyProtocolFaultException(fault);
        }
    }

    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="requestEnvelope">The request envelope.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the request async.</returns>
    public async ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope requestEnvelope,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(channel, requestEnvelope);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var headers = ReadHeaders(response);

        if (!response.IsSuccessStatusCode)
        {
            var fault = ParseProblem(body, response.ReasonPhrase);
            headers[HeaderNames.MessageType] = MessageType.Fault.ToString();
            headers[HeaderNames.ContentType] = requestEnvelope.Headers[HeaderNames.ContentType];
            return new TransportEnvelope(headers, _serializer.Serialize(fault));
        }

        headers[HeaderNames.MessageType] ??= MessageType.Response.ToString();
        return new TransportEnvelope(headers, body);
    }

    /// <summary>
    /// Subscribes a handler to envelopes received from the specified logical channel.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="handler">The callback invoked for each received message.</param>
    /// <param name="options">The options that control the operation.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the subscribe async.</returns>
    public ValueTask<IAsyncDisposable> SubscribeAsync(
        string channel,
        Func<TransportEnvelope, CancellationToken, ValueTask> handler,
        SubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("REST client transport does not support subscriptions.");

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private HttpRequestMessage CreateRequest(string channel, TransportEnvelope envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_routePrefix}/{EscapeChannel(channel)}")
        {
            Content = new ReadOnlyMemoryContent(envelope.Body)
        };

        foreach (var header in envelope.Headers)
        {
            if (string.Equals(header.Key, HeaderNames.ContentType, StringComparison.OrdinalIgnoreCase))
            {
                if (MediaTypeHeaderValue.TryParse(header.Value, out var contentType))
                {
                    request.Content.Headers.ContentType = contentType;
                }

                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return request;
    }

    private static MessageHeaders ReadHeaders(HttpResponseMessage response)
    {
        var headers = new MessageHeaders();
        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        foreach (var header in response.Content.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        return headers;
    }

    private static FaultMessage ParseProblem(byte[] body, string? reasonPhrase)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = GetString(root, "code") ?? "http_error";
            var message = GetString(root, "detail") ??
                          GetString(root, "title") ??
                          reasonPhrase ??
                          "The REST endpoint returned an error.";
            var exceptionType = GetString(root, "exceptionType");
            var retryable = root.TryGetProperty("retryable", out var retryableValue) &&
                            retryableValue.ValueKind is JsonValueKind.True;
            FaultDetail[]? details = null;
            if (root.TryGetProperty("details", out var detailsValue))
            {
                details = detailsValue.Deserialize<FaultDetail[]>(ProblemJsonOptions);
            }

            return new FaultMessage(code, message, exceptionType, retryable, details);
        }
        catch (JsonException)
        {
            return new FaultMessage(
                "http_error",
                reasonPhrase ?? "The REST endpoint returned an unreadable error.");
        }
    }

    private static string? GetString(JsonElement root, string property)
        => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string EscapeChannel(string channel)
        => string.Join(
            "/",
            channel.Split('/').Select(Uri.EscapeDataString));
}
