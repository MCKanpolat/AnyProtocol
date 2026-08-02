namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Defines operations for log writer.
/// </summary>
public interface ILogWriter
{
    /// <summary>
    /// Performs the log operation.
    /// </summary>
    /// <param name="severity">The severity.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="args">The args.</param>
    void Log(LogSeverity severity, string? message, Exception? exception, params object?[] args);

    /// <summary>
    /// Performs the log event operation.
    /// </summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="severity">The severity.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="args">The args.</param>
    void LogEvent(
        int eventId,
        LogSeverity severity,
        string? message,
        Exception? exception,
        params object?[] args)
        => Log(severity, message, exception, args);

    /// <summary>
    /// Performs the is enabled operation.
    /// </summary>
    /// <param name="severity">The severity.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    bool IsEnabled(LogSeverity severity);
}