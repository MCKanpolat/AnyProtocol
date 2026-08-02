using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;

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
    /// <param name="configuration">The configuration.</param>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    /// <param name="serializerOptions">The serializer options.</param>
    public McpToolCatalog(
        LinkConfiguration configuration,
        ContractDescriptorFactory descriptorFactory,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(descriptorFactory);
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
        foreach (var registration in configuration.ServerRegistrations
                     .Where(static registration =>
                         registration.Protocols.Contains(ProtocolKey.Mcp)))
        {
            var contract = descriptorFactory.CreateRuntimeCompatible(registration.ContractType);
            foreach (var method in contract.Methods)
            {
                var attribute = method.Method.GetCustomAttribute<McpToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                if (method.Operation == ContractOperation.Stream)
                {
                    throw new InvalidOperationException(
                        $"MCP tool '{method.ContractName}.{method.MethodName}' cannot use server streaming.");
                }

                var name = string.IsNullOrWhiteSpace(attribute.Name)
                    ? GetDefaultName(registration.ContractType, method.MethodName)
                    : attribute.Name;
                ValidateName(name!);
                tools.Add(
                    new McpToolDescriptor(
                        name!,
                        attribute.Description,
                        attribute.ReadOnly,
                        attribute.Destructive,
                        attribute.Idempotent,
                        attribute.OpenWorld,
                        CreateInputSchema(method.RequestType),
                        method.ResponseType is null
                            ? null
                            : CreateOutputSchema(method.ResponseType),
                        registration,
                        method));
            }
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

    private static string GetDefaultName(Type contractType, string methodName)
    {
        var contractName = contractType.Name;
        if (contractName.Length > 1 &&
            contractName[0] == 'I' &&
            char.IsUpper(contractName[1]))
        {
            contractName = contractName[1..];
        }

        if (methodName.EndsWith("Async", StringComparison.Ordinal))
        {
            methodName = methodName[..^5];
        }

        return $"{ToSnakeCase(contractName)}_{ToSnakeCase(methodName)}";
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) &&
                index > 0 &&
                (!char.IsUpper(value[index - 1]) ||
                 index + 1 < value.Length && char.IsLower(value[index + 1])))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
