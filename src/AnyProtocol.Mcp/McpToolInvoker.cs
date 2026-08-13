using System.Text.Json;
using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Services;

namespace AnyProtocol.Mcp;

/// <summary>
/// Provides the mcp tool invoker implementation used by AnyProtocol applications.
/// </summary>
public sealed class McpToolInvoker
{
    private readonly McpToolCatalog _catalog;
    private readonly MessageDispatcher _dispatcher;
    private readonly IMessageSerializer _serializer;
    private readonly IMcpCredentialProvider _credentialProvider;
    private readonly IMessageEnvelopeFactory _envelopeFactory;
    private readonly IRequestAdmission _admission;
    private readonly OutboundOperationLifetime _outboundLifetime;

    /// <summary>
    /// Initializes a new instance of the McpToolInvoker class.
    /// </summary>
    /// <param name="catalog">The catalog.</param>
    /// <param name="dispatcher">The dispatcher.</param>
    /// <param name="serializer">The serializer.</param>
    /// <param name="credentialProvider">The credential provider.</param>
    /// <param name="envelopeFactory">The message metadata factory.</param>
    /// <param name="admission">The shared request admission coordinator.</param>
    /// <param name="outboundLifetime">The cancellation boundary for the current bus run.</param>
    public McpToolInvoker(
        McpToolCatalog catalog,
        MessageDispatcher dispatcher,
        IMessageSerializer serializer,
        IMcpCredentialProvider credentialProvider,
        IMessageEnvelopeFactory? envelopeFactory = null,
        IRequestAdmission? admission = null,
        OutboundOperationLifetime? outboundLifetime = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _credentialProvider = credentialProvider ??
                              throw new ArgumentNullException(nameof(credentialProvider));
        _envelopeFactory = envelopeFactory ?? DefaultMessageEnvelopeFactory.CreateDefault();
        _admission = admission ?? CreateStandaloneAdmission();
        _outboundLifetime = outboundLifetime ?? new OutboundOperationLifetime();
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
        using var admissionLease = _admission.TryEnter();
        if (admissionLease is null)
        {
            return McpInvocationResult.Failure(
                "retryable_unavailable",
                "AnyProtocol is draining and cannot accept new MCP tool calls.",
                retryable: true);
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _outboundLifetime.Token);
        var operationToken = operationCancellation.Token;

        McpToolDescriptor tool;
        try
        {
            tool = _catalog.GetRequired(toolName);
        }
        catch (KeyNotFoundException exception)
        {
            return McpInvocationResult.Failure("tool_not_found", exception.Message);
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
            return McpInvocationResult.Failure("invalid_arguments", exception.Message);
        }

        var requestBody = await _serializer.SerializeAsync(
                tool.Method.RequestType,
                request,
                operationToken)
            .ConfigureAwait(false);
        var headers = _envelopeFactory.CreateOutboundHeaders(
            MessageType.Request,
            tool.Method.Channel,
            tool.Method.ContractName,
            tool.Method.MethodName,
            _serializer.GetType().FullName,
            correlationId: _envelopeFactory.CreateMessageId());
        var token = _credentialProvider.GetAuthToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            headers[HeaderNames.AuthToken] = token;
        }

        var dispatch = await _dispatcher.InvokeAsync(
                tool.Registration,
                tool.Method,
                new TransportEnvelope(headers, requestBody),
                operationToken,
                ProtocolKey.Mcp)
            .ConfigureAwait(false);
        if (dispatch.Fault is not null)
        {
            return McpInvocationResult.Failure(
                dispatch.Fault.Code,
                dispatch.Fault.Message,
                dispatch.Fault.Retryable,
                dispatch.Fault.Details);
        }

        if (tool.Method.ResponseType is null)
        {
            return McpInvocationResult.Success(null);
        }

        var result = JsonSerializer.SerializeToElement(
            dispatch.Result,
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

    private static RequestAdmissionCoordinator CreateStandaloneAdmission()
    {
        var admission = new RequestAdmissionCoordinator();
        admission.StartAccepting();
        return admission;
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

}

/// <summary>
/// Contains the outcome of mcp invocation processing.
/// </summary>
/// <param name="IsError">The is error.</param>
/// <param name="Error">The structured error, when invocation failed.</param>
/// <param name="StructuredContent">The structured content.</param>
public sealed record McpInvocationResult(
    bool IsError,
    McpToolError? Error,
    JsonElement? StructuredContent)
{
    /// <summary>Gets the machine-readable error code.</summary>
    public string? ErrorCode => Error?.Code;

    /// <summary>Gets the safe error message.</summary>
    public string? Message => Error?.Message;

    /// <summary>
    /// Performs the success operation.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The result of the success operation.</returns>
    public static McpInvocationResult Success(JsonElement? content)
        => new(false, null, content);

    /// <summary>
    /// Performs the error operation.
    /// </summary>
    /// <param name="code">The machine-readable code.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="retryable">Whether the caller can retry the operation.</param>
    /// <param name="details">Structured validation or fault details.</param>
    /// <returns>The result of the error operation.</returns>
    public static McpInvocationResult Failure(
        string code,
        string message,
        bool retryable = false,
        IReadOnlyList<FaultDetail>? details = null)
        => new(true, new McpToolError(code, message, retryable, details ?? []), null);
}

/// <summary>Contains the structured, safe error returned from an MCP tool invocation.</summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Message">The safe user-facing message.</param>
/// <param name="Retryable">Whether the caller can retry the operation.</param>
/// <param name="Details">Structured validation or fault details.</param>
public sealed record McpToolError(
    string Code,
    string Message,
    bool Retryable,
    IReadOnlyList<FaultDetail> Details);
