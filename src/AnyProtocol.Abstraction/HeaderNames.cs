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
}
