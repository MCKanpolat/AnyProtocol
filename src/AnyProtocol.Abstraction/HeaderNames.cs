namespace AnyProtocol.Abstraction;

/// <summary>
/// Provides the header names implementation used by AnyProtocol applications.
/// </summary>
public static class HeaderNames
{
    /// <summary>
    /// The message id value.
    /// </summary>
    public const string MessageId = "cl-message-id";
    /// <summary>
    /// The correlation id value.
    /// </summary>
    public const string CorrelationId = "cl-correlation-id";
    /// <summary>
    /// The reply to value.
    /// </summary>
    public const string ReplyTo = "cl-reply-to";
    /// <summary>
    /// The channel value.
    /// </summary>
    public const string Channel = "cl-channel";
    /// <summary>
    /// The contract value.
    /// </summary>
    public const string Contract = "cl-contract";
    /// <summary>
    /// The method value.
    /// </summary>
    public const string Method = "cl-method";
    /// <summary>
    /// The message type value.
    /// </summary>
    public const string MessageType = "cl-message-type";
    /// <summary>
    /// The content type value.
    /// </summary>
    public const string ContentType = "content-type";
    /// <summary>
    /// The trace parent value.
    /// </summary>
    public const string TraceParent = "traceparent";
    /// <summary>
    /// The trace state value.
    /// </summary>
    public const string TraceState = "tracestate";
    /// <summary>
    /// The auth token value.
    /// </summary>
    public const string AuthToken = "cl-auth-token";
    /// <summary>
    /// The sent at value.
    /// </summary>
    public const string SentAt = "cl-sent-at";
    /// <summary>
    /// The stream sequence value.
    /// </summary>
    public const string StreamSequence = "cl-stream-seq";
    /// <summary>
    /// The partition key value.
    /// </summary>
    public const string PartitionKey = "cl-partition-key";
    /// <summary>
    /// The dead letter source value.
    /// </summary>
    public const string DeadLetterSource = "cl-dead-letter-source";
    /// <summary>
    /// The dead letter error type value.
    /// </summary>
    public const string DeadLetterErrorType = "cl-dead-letter-error-type";
    /// <summary>
    /// The dead letter error value.
    /// </summary>
    public const string DeadLetterError = "cl-dead-letter-error";
    /// <summary>
    /// The deadline value.
    /// </summary>
    public const string Deadline = "cl-deadline";
    /// <summary>The payload delivery mode.</summary>
    public const string PayloadMode = "cl-payload-mode";
    /// <summary>The logical payload-store name.</summary>
    public const string PayloadStore = "cl-payload-store";
    /// <summary>The opaque provider locator.</summary>
    public const string PayloadKey = "cl-payload-key";
    /// <summary>The original serialized body length.</summary>
    public const string PayloadLength = "cl-payload-length";
    /// <summary>The SHA-256 digest of the original serialized body.</summary>
    public const string PayloadSha256 = "cl-payload-sha256";
    /// <summary>The UTC expiry of a stored body.</summary>
    public const string PayloadExpiresAt = "cl-payload-expires-at";
}
