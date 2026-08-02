using System.Text.Json;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;

namespace AnyProtocol.Mcp;

/// <summary>
/// Describes mcp tool metadata used at runtime.
/// </summary>
/// <param name="Name">The registered instance name.</param>
/// <param name="Description">An optional human-readable description.</param>
/// <param name="ReadOnly">The read only.</param>
/// <param name="Destructive">The destructive.</param>
/// <param name="Idempotent">The idempotent.</param>
/// <param name="OpenWorld">The open world.</param>
/// <param name="InputSchema">The input schema.</param>
/// <param name="OutputSchema">The output schema.</param>
/// <param name="Registration">The registration.</param>
/// <param name="Method">The method.</param>
public sealed record McpToolDescriptor(
    string Name,
    string? Description,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    ServerRegistration Registration,
    ContractMethodDescriptor Method);
