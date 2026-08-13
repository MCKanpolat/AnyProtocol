using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;

namespace AnyProtocol.Mcp;

/// <summary>
/// Provides the mcp tool catalog implementation used by AnyProtocol applications.
/// </summary>
public sealed class McpToolCatalog
{
    private readonly IReadOnlyDictionary<string, McpToolDescriptor> _byName;

    /// <summary>
    /// Initializes a new instance of the McpToolCatalog class.
    /// </summary>
    /// <param name="runtimePlan">The immutable runtime plan.</param>
    /// <param name="serializerOptions">The serializer options.</param>
    public McpToolCatalog(
        RuntimePlan runtimePlan,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(runtimePlan);
        SerializerOptions = serializerOptions is null
            ? new JsonSerializerOptions(JsonSerializerDefaults.Web)
            : new JsonSerializerOptions(serializerOptions);
        if (SerializerOptions.TypeInfoResolver is null)
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                throw new InvalidOperationException(
                    "MCP discovery requires source-generated JsonSerializerOptions when dynamic " +
                    "code is unavailable. Pass JsonSerializerContext.Options.");
            }

            SerializerOptions.TypeInfoResolver = JsonSerializerOptions.Default.TypeInfoResolver;
        }

        var tools = new List<McpToolDescriptor>();
        foreach (var plan in runtimePlan.McpToolPlans)
        {
            if (plan.Method.Operation == ContractOperation.Stream)
            {
                throw new InvalidOperationException(
                    $"MCP tool '{plan.Method.ContractName}.{plan.Method.MethodName}' cannot use server streaming.");
            }

            ValidateName(plan.Name);
            tools.Add(
                new McpToolDescriptor(
                    plan.Name,
                    plan.Description,
                    plan.ReadOnly,
                    plan.Destructive,
                    plan.Method.IsIdempotent,
                    plan.OpenWorld,
                    CreateInputSchema(plan.Method.RequestType),
                    plan.Method.ResponseType is null
                        ? null
                        : CreateOutputSchema(plan.Method.ResponseType),
                    plan.Registration,
                    plan.Method));
        }

        var duplicate = tools.GroupBy(static tool => tool.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"MCP tool name '{duplicate.Key}' is registered more than once.");
        }

        Tools = tools;
        _byName = tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the serializer options.
    /// </summary>
    /// <value>The serializer options.</value>
    public JsonSerializerOptions SerializerOptions { get; }

    /// <summary>
    /// Gets the tools.
    /// </summary>
    /// <value>The tools.</value>
    public IReadOnlyList<McpToolDescriptor> Tools { get; }

    /// <summary>
    /// Gets required.
    /// </summary>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the get required operation.</returns>
    public McpToolDescriptor GetRequired(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _byName.TryGetValue(name, out var tool)
            ? tool
            : throw new KeyNotFoundException($"MCP tool '{name}' is not registered.");
    }

    private JsonElement CreateInputSchema(Type requestType)
    {
        if (requestType == typeof(EmptyRequest))
        {
            return SerializeSchema(
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false
                });
        }

        return ExportObjectSchema(requestType, "MCP input");
    }

    private JsonElement CreateOutputSchema(Type responseType)
    {
        var schema = SerializerOptions.GetJsonSchemaAsNode(responseType);
        if (!IsObjectSchema(schema))
        {
            schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["result"] = schema },
                ["required"] = new JsonArray("result")
            };
        }

        schema["type"] = "object";
        return SerializeSchema(schema);
    }

    private JsonElement ExportObjectSchema(Type type, string purpose)
    {
        JsonNode schema;
        try
        {
            schema = SerializerOptions.GetJsonSchemaAsNode(type);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' cannot be used as {purpose}.",
                exception);
        }

        if (!IsObjectSchema(schema))
        {
            throw new InvalidOperationException(
                $"Type '{type.FullName}' must produce an object JSON schema for {purpose}.");
        }

        schema["type"] = "object";
        return SerializeSchema(schema);
    }

    private JsonElement SerializeSchema(JsonNode schema)
        => JsonSerializer.SerializeToElement(
            schema,
            (JsonTypeInfo<JsonNode>)SerializerOptions.GetTypeInfo(typeof(JsonNode)));

    private static bool IsObjectSchema(JsonNode schema)
    {
        var type = schema["type"];
        if (type is JsonValue value &&
            value.TryGetValue<string>(out var name))
        {
            return name == "object";
        }

        return type is JsonArray types &&
               types.Any(
                   static item =>
                       item is JsonValue itemValue &&
                       itemValue.TryGetValue<string>(out var itemName) &&
                       itemName == "object");
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 128 ||
            name.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '_' and not '-' and not '.'))
        {
            throw new InvalidOperationException(
                $"MCP tool name '{name}' must contain 1-128 ASCII letters, digits, '.', '_' or '-'.");
        }
    }

}
