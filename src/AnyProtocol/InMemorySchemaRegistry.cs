using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>Provides a deterministic in-memory registry suitable for composition and testing.</summary>
public sealed class InMemorySchemaRegistry : ISchemaRegistry
{
    private readonly Dictionary<string, List<MessageSchema>> _schemas =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMessageSchemaCompatibilityChecker> _checkers;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a registry with the built-in JSON Schema checker.</summary>
    public InMemorySchemaRegistry(
        IEnumerable<IMessageSchemaCompatibilityChecker>? checkers = null,
        TimeProvider? timeProvider = null)
    {
        _checkers = (checkers ?? [new JsonSchemaCompatibilityChecker()])
            .ToDictionary(static checker => checker.Format, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async ValueTask<MessageSchema> RegisterAsync(
        string subject,
        string format,
        string definition,
        SchemaCompatibilityMode compatibility = SchemaCompatibilityMode.Backward,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(subject, format, definition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var normalizedSubject = subject.Trim();
            if (!_schemas.TryGetValue(normalizedSubject, out var versions))
            {
                versions = [];
                _schemas.Add(normalizedSubject, versions);
            }

            var fingerprint = Fingerprint(definition);
            var current = versions.Count == 0 ? null : versions[^1];
            if (current is not null &&
                string.Equals(current.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                return current;
            }

            if (current is not null)
            {
                EnsureSameFormat(current, format);
                var result = Check(current, format, definition, compatibility);
                if (!result.IsCompatible)
                {
                    throw new SchemaCompatibilityException(
                        $"Schema '{normalizedSubject}' is not {compatibility.ToString().ToLowerInvariant()} " +
                        $"compatible with version {current.Version}: {string.Join("; ", result.Errors)}");
                }
            }
            else if (compatibility != SchemaCompatibilityMode.None)
            {
                _ = GetChecker(format).Check(
                    definition,
                    definition,
                    SchemaCompatibilityMode.Full);
            }

            var registered = new MessageSchema(
                normalizedSubject,
                versions.Count + 1,
                format.Trim(),
                definition.Trim(),
                fingerprint,
                _timeProvider.GetUtcNow());
            versions.Add(registered);
            return registered;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<SchemaCompatibilityResult> CheckCompatibilityAsync(
        string subject,
        string format,
        string candidateDefinition,
        SchemaCompatibilityMode compatibility = SchemaCompatibilityMode.Backward,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(subject, format, candidateDefinition);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_schemas.TryGetValue(subject.Trim(), out var versions) || versions.Count == 0)
            {
                if (compatibility != SchemaCompatibilityMode.None)
                {
                    _ = GetChecker(format).Check(
                        candidateDefinition,
                        candidateDefinition,
                        SchemaCompatibilityMode.Full);
                }

                return SchemaCompatibilityResult.Compatible;
            }

            var current = versions[^1];
            EnsureSameFormat(current, format);
            return Check(current, format, candidateDefinition, compatibility);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MessageSchema?> GetLatestAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _schemas.TryGetValue(subject.Trim(), out var versions) && versions.Count > 0
                ? versions[^1]
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<MessageSchema?> GetAsync(
        string subject,
        int version,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _schemas.TryGetValue(subject.Trim(), out var versions) && version <= versions.Count
                ? versions[version - 1]
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private SchemaCompatibilityResult Check(
        MessageSchema current,
        string format,
        string definition,
        SchemaCompatibilityMode compatibility)
        => compatibility == SchemaCompatibilityMode.None
            ? SchemaCompatibilityResult.Compatible
            : GetChecker(format).Check(current.Definition, definition, compatibility);

    private IMessageSchemaCompatibilityChecker GetChecker(string format)
        => _checkers.GetValueOrDefault(format.Trim()) ??
           throw new SchemaCompatibilityException(
               $"No schema compatibility checker is registered for format '{format}'.");

    private static void EnsureSameFormat(MessageSchema current, string format)
    {
        if (!string.Equals(current.Format, format.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new SchemaCompatibilityException(
                $"Schema subject '{current.Subject}' already uses format '{current.Format}' and " +
                $"cannot be registered as '{format}'.");
        }
    }

    private static void ValidateInput(string subject, string format, string definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
    }

    private static string Fingerprint(string definition)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definition.Trim())))
            .ToLowerInvariant();
}

/// <summary>
/// Applies conservative object-property compatibility rules to JSON Schema documents.
/// </summary>
public sealed class JsonSchemaCompatibilityChecker : IMessageSchemaCompatibilityChecker
{
    /// <inheritdoc />
    public string Format => "json-schema";

    /// <inheritdoc />
    public SchemaCompatibilityResult Check(
        string currentDefinition,
        string candidateDefinition,
        SchemaCompatibilityMode mode)
    {
        var current = Parse(currentDefinition, "current");
        var candidate = Parse(candidateDefinition, "candidate");
        var errors = new List<string>();
        if (mode is SchemaCompatibilityMode.Backward or SchemaCompatibilityMode.Full)
        {
            CheckReader(current, candidate, "backward", errors);
        }

        if (mode is SchemaCompatibilityMode.Forward or SchemaCompatibilityMode.Full)
        {
            CheckReader(candidate, current, "forward", errors);
        }

        return errors.Count == 0
            ? SchemaCompatibilityResult.Compatible
            : new SchemaCompatibilityResult(false, errors);
    }

    private static void CheckReader(
        SchemaShape writer,
        SchemaShape reader,
        string direction,
        ICollection<string> errors)
    {
        foreach (var required in reader.Required)
        {
            if (!writer.Required.Contains(required))
            {
                errors.Add($"{direction}: required property '{required}' is not guaranteed by the writer");
            }
        }

        foreach (var property in writer.Properties)
        {
            if (reader.Properties.TryGetValue(property.Key, out var readerType) &&
                !string.Equals(property.Value, readerType, StringComparison.Ordinal))
            {
                errors.Add(
                    $"{direction}: property '{property.Key}' changes type from " +
                    $"'{property.Value}' to '{readerType}'");
            }
        }
    }

    private static SchemaShape Parse(string definition, string label)
    {
        try
        {
            using var document = JsonDocument.Parse(definition);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var rootType) ||
                rootType.GetString() != "object")
            {
                throw new SchemaCompatibilityException(
                    $"The {label} JSON Schema must describe an object.");
            }

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("properties", out var propertyElement))
            {
                if (propertyElement.ValueKind != JsonValueKind.Object)
                {
                    throw new SchemaCompatibilityException(
                        $"The {label} JSON Schema 'properties' value must be an object.");
                }

                foreach (var property in propertyElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Object ||
                        !property.Value.TryGetProperty("type", out var typeElement) ||
                        typeElement.ValueKind != JsonValueKind.String)
                    {
                        throw new SchemaCompatibilityException(
                            $"The {label} property '{property.Name}' must declare one string 'type'.");
                    }

                    properties.Add(property.Name, typeElement.GetString()!);
                }
            }

            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredElement))
            {
                if (requiredElement.ValueKind != JsonValueKind.Array)
                {
                    throw new SchemaCompatibilityException(
                        $"The {label} JSON Schema 'required' value must be an array.");
                }

                foreach (var item in requiredElement.EnumerateArray())
                {
                    var name = item.GetString() ??
                               throw new SchemaCompatibilityException(
                                   $"The {label} JSON Schema contains a non-string required property.");
                    if (!properties.ContainsKey(name))
                    {
                        throw new SchemaCompatibilityException(
                            $"The {label} required property '{name}' is not declared in properties.");
                    }

                    required.Add(name);
                }
            }

            return new SchemaShape(properties, required);
        }
        catch (JsonException exception)
        {
            throw new SchemaCompatibilityException(
                $"The {label} JSON Schema is invalid JSON: {exception.Message}");
        }
    }

    private sealed record SchemaShape(
        IReadOnlyDictionary<string, string> Properties,
        IReadOnlySet<string> Required);
}
