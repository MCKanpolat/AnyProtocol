using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>Controls how a failed message body is retained in a dead-letter record.</summary>
public enum DeadLetterBodyPolicy
{
    /// <summary>Does not retain the body.</summary>
    Omit,
    /// <summary>Retains the body for an explicitly approved replay domain.</summary>
    Preserve,
    /// <summary>Retains only a non-reversible fingerprint of the body.</summary>
    FingerprintOnly
}

/// <summary>Classifies message header names that must not cross a dead-letter trust boundary.</summary>
public interface IMessageHeaderSanitizer
{
    /// <summary>Returns whether the named header contains sensitive data.</summary>
    bool IsSensitive(string headerName);
}

/// <summary>Creates a provider-neutral, sanitized dead-letter envelope.</summary>
public interface IDeadLetterEnvelopeFactory
{
    /// <summary>Creates a detached envelope suitable for dead-letter delivery.</summary>
    DeadLetterEnvelope Create(string source, TransportEnvelope envelope, Exception exception);
}

/// <summary>Contains sanitized dead-letter metadata and an optional retained body.</summary>
public sealed record DeadLetterEnvelope(
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Body);

/// <summary>
/// Default case-insensitive credential classifier for framework and common HTTP credentials.
/// </summary>
public sealed class DefaultMessageHeaderSanitizer : IMessageHeaderSanitizer
{
    private static readonly string[] SensitiveFragments =
    [
        "authorization", "auth-token", "apikey", "api-key", "access-key", "secret",
        "password", "passwd", "cookie", "session", "credential"
    ];

    private readonly HashSet<string> _additionalSensitiveHeaders;

    /// <summary>Initializes the sanitizer with optional application-specific sensitive headers.</summary>
    public DefaultMessageHeaderSanitizer(IEnumerable<string>? additionalSensitiveHeaders = null)
    {
        _additionalSensitiveHeaders = new HashSet<string>(
            additionalSensitiveHeaders ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public bool IsSensitive(string headerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        return _additionalSensitiveHeaders.Contains(headerName) ||
            SensitiveFragments.Any(
                fragment => headerName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Produces dead-letter envelopes with an explicit metadata allowlist and secure body defaults.
/// </summary>
public sealed class DeadLetterEnvelopeFactory : IDeadLetterEnvelopeFactory
{
    private static readonly HashSet<string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.MessageId,
        HeaderNames.CorrelationId,
        HeaderNames.Channel,
        HeaderNames.Contract,
        HeaderNames.Method,
        HeaderNames.MessageType,
        HeaderNames.ContentType,
        HeaderNames.TraceParent,
        HeaderNames.TraceState,
        HeaderNames.PartitionKey,
        HeaderNames.PayloadMode,
        HeaderNames.PayloadStore,
        HeaderNames.PayloadLength,
        HeaderNames.PayloadSha256,
        HeaderNames.PayloadExpiresAt
    };

    private readonly IMessageHeaderSanitizer _headerSanitizer;
    private readonly DeadLetterBodyPolicy _bodyPolicy;
    private readonly byte[]? _fingerprintKey;

    /// <summary>Initializes a factory with secure omission as the default body policy.</summary>
    public DeadLetterEnvelopeFactory(
        IMessageHeaderSanitizer? headerSanitizer = null,
        DeadLetterBodyPolicy bodyPolicy = DeadLetterBodyPolicy.Omit,
        ReadOnlySpan<byte> fingerprintKey = default)
    {
        if (bodyPolicy == DeadLetterBodyPolicy.FingerprintOnly && fingerprintKey.IsEmpty)
        {
            throw new ArgumentException("Fingerprint-only retention requires a non-empty HMAC key.", nameof(fingerprintKey));
        }

        _headerSanitizer = headerSanitizer ?? new DefaultMessageHeaderSanitizer();
        _bodyPolicy = bodyPolicy;
        _fingerprintKey = fingerprintKey.IsEmpty ? null : fingerprintKey.ToArray();
    }

    /// <inheritdoc />
    public DeadLetterEnvelope Create(string source, TransportEnvelope envelope, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(exception);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in envelope.Headers)
        {
            if (AllowedHeaders.Contains(header.Key) && !_headerSanitizer.IsSensitive(header.Key))
            {
                headers[header.Key] = header.Value;
            }
        }

        headers[HeaderNames.DeadLetterSource] = source;
        headers[HeaderNames.DeadLetterErrorType] = exception.GetType().Name;
        headers[HeaderNames.DeadLetterError] = "handler_failed";

        var body = _bodyPolicy == DeadLetterBodyPolicy.Preserve
            ? envelope.Body.ToArray()
            : ReadOnlyMemory<byte>.Empty;
        if (_bodyPolicy == DeadLetterBodyPolicy.FingerprintOnly)
        {
            headers[HeaderNames.DeadLetterBodyFingerprint] =
                Convert.ToHexString(HMACSHA256.HashData(_fingerprintKey!, envelope.Body.Span));
        }

        return new DeadLetterEnvelope(
            new ReadOnlyDictionary<string, string>(headers),
            body);
    }
}
