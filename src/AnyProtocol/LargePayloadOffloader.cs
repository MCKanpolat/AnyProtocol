using System.Globalization;
using System.Security.Cryptography;
using System.Diagnostics;
using AnyProtocol.Abstraction;
using AnyProtocol.Configuration;
using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>Replaces oversized serialized bodies with provider-neutral references.</summary>
public sealed class LargePayloadOffloader
{
    private readonly LargePayloadOffloadPolicy? _policy;
    private readonly LargePayloadStoreRegistry _stores;

    /// <summary>Creates an offloader for the optional compiled policy.</summary>
    public LargePayloadOffloader(
        LargePayloadOffloadPolicy? policy,
        LargePayloadStoreRegistry stores)
    {
        _policy = policy;
        _stores = stores ?? throw new ArgumentNullException(nameof(stores));
        if (policy is not null && !_stores.Contains(policy.StoreName))
        {
            throw new LargePayloadException(
                LargePayloadFailureCodes.StoreNotConfigured,
                $"Large-payload store '{policy.StoreName}' is not configured.");
        }
    }

    /// <summary>Gets the active policy, or null when offload is disabled.</summary>
    public LargePayloadOffloadPolicy? Policy => _policy;

    /// <summary>Stores an oversized body before returning its reference envelope.</summary>
    public async ValueTask<TransportEnvelope> OffloadAsync(
        TransportEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (_policy is null || envelope.Body.Length <= _policy.MaxInlinePayloadBytes)
        {
            AnyProtocolDiagnostics.RecordPayloadMessage("inline", "outbound");
            return envelope;
        }

        if (envelope.Body.Length > _policy.MaxStoredPayloadBytes)
        {
            throw Failure(
                LargePayloadFailureCodes.TooLarge,
                $"Serialized payload length {envelope.Body.Length} exceeds the configured maximum " +
                $"of {_policy.MaxStoredPayloadBytes} bytes.");
        }

        var messageId = envelope.Headers[HeaderNames.MessageId];
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw Failure(
                LargePayloadFailureCodes.InvalidReference,
                "A message ID is required before a payload can be offloaded.");
        }

        var digest = Convert.ToHexString(SHA256.HashData(envelope.Body.Span)).ToLowerInvariant();
        StoredPayloadReference reference;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            reference = await _stores.GetRequired(_policy.StoreName)
                .StoreAsync(
                    messageId,
                    envelope.Body,
                    new PayloadWriteOptions(_policy.TimeToLive, envelope.Body.Length, digest),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LargePayloadException exception)
        {
            AnyProtocolDiagnostics.RecordPayloadFailure(exception.Code, "write");
            throw;
        }
        catch (Exception exception)
        {
            AnyProtocolDiagnostics.RecordPayloadFailure(
                LargePayloadFailureCodes.StoreUnavailable,
                "write");
            throw new LargePayloadException(
                LargePayloadFailureCodes.StoreUnavailable,
                "The payload store could not persist the serialized body.",
                retryable: true,
                exception);
        }
        AnyProtocolDiagnostics.RecordPayloadStoreOperation(
            "write",
            reference.StoreName,
            envelope.Body.Length,
            startedAt);
        try
        {
            ValidateReference(reference, _policy.StoreName, envelope.Body.Length, digest);
        }
        catch (LargePayloadException exception)
        {
            AnyProtocolDiagnostics.RecordPayloadFailure(exception.Code, "write");
            throw;
        }

        envelope.Headers[HeaderNames.PayloadMode] = "stored";
        envelope.Headers[HeaderNames.PayloadStore] = reference.StoreName;
        envelope.Headers[HeaderNames.PayloadKey] = reference.Key;
        envelope.Headers[HeaderNames.PayloadLength] =
            reference.Length.ToString(CultureInfo.InvariantCulture);
        envelope.Headers[HeaderNames.PayloadSha256] = reference.Sha256;
        envelope.Headers[HeaderNames.PayloadExpiresAt] =
            reference.ExpiresAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        AnyProtocolDiagnostics.RecordPayloadMessage("stored", "outbound");
        return new TransportEnvelope(envelope.Headers, ReadOnlyMemory<byte>.Empty);
    }

    private static void ValidateReference(
        StoredPayloadReference reference,
        string expectedStore,
        long expectedLength,
        string expectedDigest)
    {
        if (!string.Equals(reference.StoreName, expectedStore, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(reference.Key) ||
            reference.Length != expectedLength ||
            !string.Equals(reference.Sha256, expectedDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw new LargePayloadException(
                LargePayloadFailureCodes.IntegrityFailed,
                "The payload store returned metadata that does not match the serialized body.");
        }
    }

    private static LargePayloadException Failure(string code, string message)
    {
        AnyProtocolDiagnostics.RecordPayloadFailure(code, "write");
        return new LargePayloadException(code, message);
    }
}
