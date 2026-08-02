using System.Text.Json;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol.Mcp;

/// <summary>
/// Provides the mcp tool invoker implementation used by AnyProtocol applications.
/// </summary>
public sealed class McpToolInvoker
{
    private const string ReplyChannel = "_anyprotocol.mcp.response";
    private readonly McpToolCatalog _catalog;
    private readonly MessageDispatcher _dispatcher;
    private readonly IMessageSerializer _serializer;
    private readonly IMcpCredentialProvider _credentialProvider;

    /// <summary>
    /// Initializes a new instance of the McpToolInvoker class.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="credentialProvider">The credential provider.</param>
    public McpToolInvoker(
        McpToolCatalog catalog,
        MessageDispatcher dispatcher,
        IMessageSerializer serializer,
        IMcpCredentialProvider credentialProvider)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _credentialProvider = credentialProvider ??
                              throw new ArgumentNullException(nameof(credentialProvider));
    }

    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="toolName">The tool name.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the invoke async.</returns>
    public async ValueTask<McpInvocationResult> InvokeAsync(
        string toolName,
        IReadOnlyDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        McpToolDescriptor tool;
        try
        {
            tool = _catalog.GetRequired(toolName);
        }
        catch (KeyNotFoundException exception)
        {
            return McpInvocationResult.Error("tool_not_found", exception.Message);
        }

        object request;
        try
        {
            request = tool.Method.RequestType == typeof(EmptyRequest)
                ? EmptyRequest.Instance
                : JsonSerializer.Deserialize(
                      SerializeArguments(arguments),
                      _catalog.SerializerOptions.GetTypeInfo(tool.Method.RequestType)) ??
                  throw new JsonException("The MCP tool arguments produced a null request.");
        }
        catch (JsonException exception)
        {
            return McpInvocationResult.Error("invalid_arguments", exception.Message);
        }

        var requestBody = await _serializer.SerializeAsync(
                tool.Method.RequestType,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var headers = new MessageHeaders
        {
            [HeaderNames.MessageId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.CorrelationId] = Guid.NewGuid().ToString("N"),
            [HeaderNames.ReplyTo] = ReplyChannel,
            [HeaderNames.Channel] = tool.Method.Channel,
            [HeaderNames.Contract] = tool.Method.ContractName,
            [HeaderNames.Method] = tool.Method.MethodName,
            [HeaderNames.MessageType] = MessageType.Request.ToString(),
            [HeaderNames.ContentType] = _serializer.GetType().FullName
        };
        var token = _credentialProvider.GetAuthToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers[HeaderNames.AuthToken] = token;
        }

        var capture = new CaptureProtocol();
        await _dispatcher.DispatchAsync(
                tool.Registration,
                tool.Method,
                new TransportEnvelope(headers, requestBody),
                capture,
                cancellationToken,
                ProtocolKey.Mcp)
            .ConfigureAwait(false);
        if (capture.Response is null)
        {
            return McpInvocationResult.Success(null);
        }

        var messageType = capture.Response.Headers.Get(
            HeaderNames.MessageType,
            MessageType.Fault);
        if (messageType == MessageType.Fault)
        {
            var fault = await _serializer.DeserializeAsync<FaultMessage>(
                    capture.Response.Body,
                    cancellationToken)
                .ConfigureAwait(false);
            return McpInvocationResult.Error(
                fault?.Code ?? "tool_error",
                fault?.Message ?? "The AnyProtocol tool failed.");
        }

        if (tool.Method.ResponseType is null)
        {
            return McpInvocationResult.Success(null);
        }

        var response = await _serializer.DeserializeAsync(
                tool.Method.ResponseType,
                capture.Response.Body,
                cancellationToken)
            .ConfigureAwait(false);
        var result = JsonSerializer.SerializeToElement(
            response,
            _catalog.SerializerOptions.GetTypeInfo(tool.Method.ResponseType));
        return McpInvocationResult.Success(
            result.ValueKind == JsonValueKind.Object
                ? result
                : WrapResult(result));
    }

    private static byte[] SerializeArguments(
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (arguments is not null)
            {
                foreach (var argument in arguments)
                {
                    writer.WritePropertyName(argument.Key);
                    argument.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static JsonElement WrapResult(JsonElement result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("result");
            result.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private sealed class CaptureProtocol : IMessagingProtocol
    {
        public TransportEnvelope? Response { get; private set; }

        public TransportCapabilities Capabilities => TransportCapabilities.NativeHeaders;

        public ValueTask SendAsync(
            string channel,
            TransportEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            if (channel != ReplyChannel)
            {
                throw new InvalidOperationException(
                    $"Unexpected MCP reply channel '{channel}'.");
            }

            Response = envelope;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(
            string channel,
            Func<TransportEnvelope, CancellationToken, ValueTask> handler,
            SubscriptionOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Contains the outcome of mcp invocation processing.
/// </summary>
/// <param name="IsError">The is error.</param>
/// <param name="ErrorCode">The error code.</param>
/// <param name="Message">The message payload or description.</param>
/// <param name="StructuredContent">The structured content.</param>
public sealed record McpInvocationResult(
    bool IsError,
    string? ErrorCode,
    string? Message,
    JsonElement? StructuredContent)
{
    /// <summary>
    /// Performs the success operation.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The result of the success operation.</returns>
    public static McpInvocationResult Success(JsonElement? content)
        => new(false, null, null, content);

    /// <summary>
    /// Performs the error operation.
    /// </summary>
    /// <param name="code">The machine-readable code.</param>
    /// <param name="message">The message payload or description.</param>
    /// <returns>The result of the error operation.</returns>
    public static McpInvocationResult Error(string code, string message)
        => new(true, code, message, null);
}
