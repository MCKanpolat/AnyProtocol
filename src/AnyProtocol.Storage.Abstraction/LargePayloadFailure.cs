namespace AnyProtocol.Storage.Abstraction;

/// <summary>Stable large-payload failure codes used in faults and telemetry.</summary>
public static class LargePayloadFailureCodes
{
    /// <summary>The referenced payload is missing or expired.</summary>
    public const string NotFound = "payload_not_found";
    /// <summary>The configured store is temporarily unavailable.</summary>
    public const string StoreUnavailable = "payload_store_unavailable";
    /// <summary>The payload does not match its integrity metadata.</summary>
    public const string IntegrityFailed = "payload_integrity_failed";
    /// <summary>The message ID is already associated with different content.</summary>
    public const string IdConflict = "payload_id_conflict";
    /// <summary>The named store is not configured.</summary>
    public const string StoreNotConfigured = "payload_store_not_configured";
    /// <summary>The reference envelope is invalid.</summary>
    public const string InvalidReference = "payload_reference_invalid";
    /// <summary>The serialized payload exceeds its configured bound.</summary>
    public const string TooLarge = "payload_too_large";
}

/// <summary>Represents a provider or wire failure while offloading a serialized body.</summary>
public sealed class LargePayloadException : Exception
{
    /// <summary>Creates a stable large-payload failure.</summary>
    public LargePayloadException(
        string code,
        string message,
        bool retryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Retryable = retryable;
    }

    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; }

    /// <summary>Gets whether retrying may succeed without changing the message.</summary>
    public bool Retryable { get; }
}
