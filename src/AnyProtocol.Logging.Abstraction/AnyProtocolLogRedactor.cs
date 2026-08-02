using System.Data.Common;

namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Provides the anyprotocol log redactor implementation used by AnyProtocol applications.
/// </summary>
public static class AnyProtocolLogRedactor
{
    /// <summary>
    /// The redacted value.
    /// </summary>
    public const string Redacted = "[REDACTED]";
    /// <summary>
    /// The payload omitted value.
    /// </summary>
    public const string PayloadOmitted = "[PAYLOAD OMITTED]";

    private static readonly string[] SensitiveNames =
    [
        "authorization",
        "auth",
        "token",
        "password",
        "passwd",
        "pwd",
        "secret",
        "credential",
        "connectionstring",
        "connection-string",
        "apikey",
        "api-key",
        "accesskey",
        "access-key",
        "cl-auth-token"
    ];

    /// <summary>Redacts values whose names indicate credentials or authentication data.</summary>
    public static string? RedactValue(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return IsSensitiveName(name) && value is not null ? Redacted : value;
    }

    /// <summary>Redacts credential-bearing values in a connection string.</summary>
    public static string RedactConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return string.Empty;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in builder.Keys.Cast<string>().ToArray())
            {
                if (IsSensitiveName(key))
                {
                    builder[key] = Redacted;
                }
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return Redacted;
        }
    }

    /// <summary>Returns an omitted marker unless payload logging was explicitly enabled.</summary>
    public static string Payload(bool includePayload, ReadOnlyMemory<byte> payload)
        => includePayload
            ? Convert.ToBase64String(payload.Span)
            : PayloadOmitted;

    private static bool IsSensitiveName(string name)
    {
        var normalized = string.Concat(
            name.Where(static character => char.IsAsciiLetterOrDigit(character)))
            .ToLowerInvariant();
        return SensitiveNames.Any(
            candidate =>
                normalized.Contains(
                    string.Concat(
                        candidate.Where(static character => char.IsAsciiLetterOrDigit(character))),
                    StringComparison.Ordinal));
    }
}
