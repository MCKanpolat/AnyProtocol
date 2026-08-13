namespace AnyProtocol.Abstraction;

/// <summary>Defines the compatibility guarantee required for a schema change.</summary>
public enum SchemaCompatibilityMode
{
    /// <summary>Bypasses compatibility comparison.</summary>
    None,
    /// <summary>Requires new consumers to read data written with the previous schema.</summary>
    Backward,
    /// <summary>Requires previous consumers to read data written with the new schema.</summary>
    Forward,
    /// <summary>Requires both backward and forward compatibility.</summary>
    Full
}

/// <summary>Describes one immutable, versioned message schema.</summary>
/// <param name="Subject">The stable contract or event subject.</param>
/// <param name="Version">The monotonically increasing subject version.</param>
/// <param name="Format">The schema format understood by a compatibility checker.</param>
/// <param name="Definition">The provider-neutral schema definition.</param>
/// <param name="Fingerprint">The SHA-256 fingerprint of the definition.</param>
/// <param name="RegisteredAt">The UTC registration timestamp.</param>
public sealed record MessageSchema(
    string Subject,
    int Version,
    string Format,
    string Definition,
    string Fingerprint,
    DateTimeOffset RegisteredAt);

/// <summary>Reports whether a candidate schema satisfies one compatibility mode.</summary>
/// <param name="IsCompatible">Whether the candidate is compatible.</param>
/// <param name="Errors">Actionable incompatibility descriptions.</param>
public sealed record SchemaCompatibilityResult(
    bool IsCompatible,
    IReadOnlyList<string> Errors)
{
    /// <summary>Creates a successful compatibility result.</summary>
    public static SchemaCompatibilityResult Compatible { get; } = new(true, []);
}

/// <summary>Evaluates compatibility for one schema format.</summary>
public interface IMessageSchemaCompatibilityChecker
{
    /// <summary>Gets the case-insensitive format name handled by this checker.</summary>
    string Format { get; }

    /// <summary>Checks a candidate definition against the current schema.</summary>
    SchemaCompatibilityResult Check(
        string currentDefinition,
        string candidateDefinition,
        SchemaCompatibilityMode mode);
}

/// <summary>Stores and validates versioned message schemas independently of a provider.</summary>
public interface ISchemaRegistry
{
    /// <summary>Registers a compatible version, returning the existing version for an identical definition.</summary>
    ValueTask<MessageSchema> RegisterAsync(
        string subject,
        string format,
        string definition,
        SchemaCompatibilityMode compatibility = SchemaCompatibilityMode.Backward,
        CancellationToken cancellationToken = default);

    /// <summary>Checks a candidate without changing registry state.</summary>
    ValueTask<SchemaCompatibilityResult> CheckCompatibilityAsync(
        string subject,
        string format,
        string candidateDefinition,
        SchemaCompatibilityMode compatibility = SchemaCompatibilityMode.Backward,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the latest registered version, or <see langword="null"/>.</summary>
    ValueTask<MessageSchema?> GetLatestAsync(
        string subject,
        CancellationToken cancellationToken = default);

    /// <summary>Gets one exact subject version, or <see langword="null"/>.</summary>
    ValueTask<MessageSchema?> GetAsync(
        string subject,
        int version,
        CancellationToken cancellationToken = default);
}

/// <summary>Raised when a schema cannot be parsed or violates compatibility policy.</summary>
public sealed class SchemaCompatibilityException : Exception
{
    /// <summary>Creates a schema compatibility error.</summary>
    public SchemaCompatibilityException(string message)
        : base(message)
    {
    }
}
