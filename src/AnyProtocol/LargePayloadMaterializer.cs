using System.Security.Cryptography;
using System.Diagnostics;
using AnyProtocol.Abstraction;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>Hydrates and verifies stored-payload references before deserialization.</summary>
public sealed class LargePayloadMaterializer(
    LargePayloadStoreRegistry stores,
    IDateTimeProvider clock)
{
    /// <summary>Returns inline envelopes unchanged and hydrates stored references.</summary>
    public async ValueTask<TransportEnvelope> MaterializeAsync(
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var mode = envelope.Headers[HeaderNames.PayloadMode];
        if (string.IsNullOrWhiteSpace(mode) ||
            string.Equals(mode, "inline", StringComparison.OrdinalIgnoreCase))
        {
            AnyProtocolDiagnostics.RecordPayloadMessage("inline", "inbound");
            return envelope;
        }

        if (!string.Equals(mode, "stored", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidReference("The payload mode is not supported.");
        }

        var storeName = Required(envelope, HeaderNames.PayloadStore);
        var key = Required(envelope, HeaderNames.PayloadKey);
        var digest = Required(envelope, HeaderNames.PayloadSha256);
        if (!long.TryParse(envelope.Headers[HeaderNames.PayloadLength], out var length) || length < 0 ||
            !DateTimeOffset.TryParse(envelope.Headers[HeaderNames.PayloadExpiresAt], out var expiresAt))
        {
            throw InvalidReference("Stored payload length or expiry metadata is invalid.");
        }

        if (expiresAt <= clock.GetUtcNow())
        {
            throw Failure(
                LargePayloadFailureCodes.NotFound,
                "The stored payload reference has expired.");
        }

        var reference = new StoredPayloadReference(storeName, key, length, digest, expiresAt);
        ReadOnlyMemory<byte> body;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            body = await stores.GetRequired(storeName)
                .LoadAsync(reference, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LargePayloadException exception)
        {
            AnyProtocolDiagnostics.RecordPayloadFailure(exception.Code, "read");
            throw;
        }
        catch (Exception exception)
        {
            AnyProtocolDiagnostics.RecordPayloadFailure(
                LargePayloadFailureCodes.StoreUnavailable,
                "read");
            throw new LargePayloadException(
                LargePayloadFailureCodes.StoreUnavailable,
                "The payload store could not hydrate the serialized body.",
                retryable: true,
                exception);
        }
        AnyProtocolDiagnostics.RecordPayloadStoreOperation(
            "read",
            storeName,
            body.Length,
            startedAt);
        if (body.Length != length)
        {
            throw IntegrityFailure();
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(body.Span)).ToLowerInvariant();
        if (!string.Equals(actualDigest, digest, StringComparison.OrdinalIgnoreCase))
        {
            throw IntegrityFailure();
        }

        AnyProtocolDiagnostics.RecordPayloadMessage("stored", "inbound");
        if (envelope.Headers.TryGet<DateTimeOffset>(HeaderNames.SentAt, out var sentAt))
        {
            AnyProtocolDiagnostics.RecordPayloadAge(sentAt, clock.GetUtcNow());
        }
        return new TransportEnvelope(envelope.Headers, body);
    }

    private static string Required(TransportEnvelope envelope, string header)
    {
        var value = envelope.Headers[header];
        return string.IsNullOrWhiteSpace(value)
            ? throw InvalidReference($"Stored payload header '{header}' is required.")
            : value;
    }

    private static LargePayloadException InvalidReference(string message)
        => Failure(LargePayloadFailureCodes.InvalidReference, message);

    private static LargePayloadException IntegrityFailure()
        => Failure(
            LargePayloadFailureCodes.IntegrityFailed,
            "The hydrated payload does not match its declared length and SHA-256 digest.");

    private static LargePayloadException Failure(string code, string message)
    {
        AnyProtocolDiagnostics.RecordPayloadFailure(code, "read");
        return new LargePayloadException(code, message);
    }
}
