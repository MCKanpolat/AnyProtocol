namespace AnyProtocol.Abstraction;

/// <summary>
/// Describes a failed message operation for an error handler.
/// </summary>
/// <param name="message">The immutable operation view available to error handlers.</param>
/// <param name="exception">The exception that caused the failure.</param>
public sealed class ErrorContext(IMessageContext message, Exception exception)
{
    /// <summary>Gets the operation that failed.</summary>
    public IMessageContext Message { get; } = message ?? throw new ArgumentNullException(nameof(message));

    /// <summary>Gets the exception that caused the failure.</summary>
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}
