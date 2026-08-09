using System.Reflection;
using System.Net.Http.Headers;
using System.Text.Json;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol.Protocol.Rest;

/// <summary>
/// Implements rest messaging messaging transport operations.
/// </summary>
public sealed class RestMessagingProtocol :
    IMessagingProtocol,
    INativeRequestReplyTransport,
    IMethodAwareMessagingProtocol,
    IMethodAwareRequestReplyTransport
{
    private static readonly JsonSerializerOptions ProblemJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly HttpClient _httpClient;
    private readonly IMessageSerializer _serializer;
    private readonly string _routePrefix;
    private readonly Func<ContractMethodDescriptor, string?>? _httpMethodResolver;

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
        : this(httpClient, serializer, routePrefix, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the RestMessagingProtocol class.
    /// </summary>
    /// <param name="httpClient">The http client.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="routePrefix">The route prefix.</param>
    /// <param name="httpMethodResolver">The optional fallback HTTP method resolver.</param>
    public RestMessagingProtocol(
        HttpClient httpClient,
        IMessageSerializer serializer,
        string routePrefix,
        Func<ContractMethodDescriptor, string?>? httpMethodResolver)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        var normalizedPrefix = routePrefix.Trim('/');
        _routePrefix = normalizedPrefix.Length == 0 ? string.Empty : $"/{normalizedPrefix}";
        _httpMethodResolver = httpMethodResolver;
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
        : this(httpClientFactory, serializer, clientName, routePrefix, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the RestMessagingProtocol class.
    /// </summary>
    /// <param name="httpClientFactory">The http client factory.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="clientName">The client name.</param>
    /// <param name="routePrefix">The route prefix.</param>
    /// <param name="httpMethodResolver">The optional fallback HTTP method resolver.</param>
    public RestMessagingProtocol(
        IHttpClientFactory httpClientFactory,
        IMessageSerializer serializer,
        string clientName,
        string routePrefix,
        Func<ContractMethodDescriptor, string?>? httpMethodResolver)
        : this(
            httpClientFactory?.CreateClient(clientName) ??
            throw new ArgumentNullException(nameof(httpClientFactory)),
            serializer,
            routePrefix,
            httpMethodResolver)
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
        => await SendAsyncCore(channel, envelope, "POST", cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Sends a transport envelope using the HTTP method resolved for the contract method.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <param name="method">The contract method metadata.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask SendAsync(
        string channel,
        TransportEnvelope envelope,
        ContractMethodDescriptor method,
        CancellationToken cancellationToken = default)
        => SendAsyncCore(channel, envelope, ResolveHttpMethod(method), cancellationToken);

    private async ValueTask SendAsyncCore(
        string channel,
        TransportEnvelope envelope,
        string httpMethod,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(channel, envelope, httpMethod);
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
        => await RequestAsyncCore(
                channel,
                requestEnvelope,
                "POST",
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Sends a request using the HTTP method resolved for the contract method.
    /// </summary>
    /// <param name="channel">The logical message channel.</param>
    /// <param name="requestEnvelope">The request envelope.</param>
    /// <param name="method">The contract method metadata.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the response envelope.</returns>
    public ValueTask<TransportEnvelope> RequestAsync(
        string channel,
        TransportEnvelope requestEnvelope,
        ContractMethodDescriptor method,
        CancellationToken cancellationToken = default)
        => RequestAsyncCore(
            channel,
            requestEnvelope,
            ResolveHttpMethod(method),
            cancellationToken);

    private async ValueTask<TransportEnvelope> RequestAsyncCore(
        string channel,
        TransportEnvelope requestEnvelope,
        string httpMethod,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(channel, requestEnvelope, httpMethod);
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

    private HttpRequestMessage CreateRequest(
        string channel,
        TransportEnvelope envelope,
        string httpMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        var request = new HttpRequestMessage(
            new HttpMethod(httpMethod),
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

    private string ResolveHttpMethod(ContractMethodDescriptor method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var candidates = method.Method
            .GetCustomAttributes<RestHttpMethodAttribute>(inherit: true)
            .Select(attribute => new HttpMethodCandidate(attribute.Method, "RestHttpMethodAttribute"))
            .Concat(GetAspNetHttpMethodCandidates(method.Method))
            .Select(candidate => new HttpMethodCandidate(
                NormalizeHttpMethod(candidate.Method, candidate.Source),
                candidate.Source))
            .DistinctBy(static candidate => candidate.Method, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple HTTP methods are configured for " +
                $"'{method.ContractName}.{method.MethodName}': " +
                string.Join(
                    ", ",
                    candidates.Select(candidate =>
                        $"'{candidate.Method}' ({candidate.Source})")) + ". " +
                "Configure exactly one HTTP method for each REST operation.");
        }

        if (candidates.Length == 1)
        {
            return candidates[0].Method;
        }

        var fallback = _httpMethodResolver?.Invoke(method) ?? "POST";
        return NormalizeHttpMethod(fallback, "fallback HTTP method");
    }

    private static IEnumerable<HttpMethodCandidate> GetAspNetHttpMethodCandidates(
        MethodInfo method)
    {
        foreach (var attribute in method.GetCustomAttributes(inherit: true))
        {
            var attributeType = attribute.GetType();
            if (attributeType.Namespace is null ||
                !attributeType.Namespace.StartsWith(
                    "Microsoft.AspNetCore.Mvc",
                    StringComparison.Ordinal))
            {
                continue;
            }

            var property = attributeType.GetProperty("HttpMethods");
            if (property?.GetValue(attribute) is not IEnumerable<string> methods)
            {
                continue;
            }

            foreach (var httpMethod in methods)
            {
                yield return new HttpMethodCandidate(
                    httpMethod,
                    $"{attributeType.Name}");
            }
        }
    }

    private static string NormalizeHttpMethod(string method, string source)
    {
        var normalized = method.Trim().ToUpperInvariant();
        if (normalized.Length == 0 ||
            normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                !"!#$%&'*+-.^_`|~".Contains(character)))
        {
            throw new InvalidOperationException(
                $"The HTTP method '{method}' from {source} is not a valid HTTP method token.");
        }

        return normalized;
    }

    private sealed record HttpMethodCandidate(string Method, string Source);

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
