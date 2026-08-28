using Microsoft.Extensions.Logging;
using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol.Logging.Microsoft;

/// <summary>
/// Provides the microsoft log writer implementation used by AnyProtocol applications.
/// </summary>
public class MicrosoftLogWriter : ILogWriter
{
    private readonly ILogger _logger;
    private readonly ExceptionLoggingMode _exceptionLoggingMode;
    private readonly IExceptionSanitizer _exceptionSanitizer;

    /// <summary>
    /// Initializes a new instance of the MicrosoftLogWriter class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exceptionLoggingMode">The exception detail policy for this sink.</param>
    /// <param name="exceptionSanitizer">The optional exception metadata sanitizer.</param>
    public MicrosoftLogWriter(
        ILogger logger,
        ExceptionLoggingMode exceptionLoggingMode = ExceptionLoggingMode.TypeOnly,
        IExceptionSanitizer? exceptionSanitizer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _exceptionLoggingMode = exceptionLoggingMode;
        _exceptionSanitizer = exceptionSanitizer ?? new DefaultExceptionSanitizer();
    }

    /// <summary>
    /// Performs the log operation.
    /// </summary>
    /// <param name="severity">The severity.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="args">The args.</param>
    public void Log(LogSeverity severity, string? message, Exception? exception, params object?[] args)
    {
        var logLevel = LogLevelMapper.Map(severity);

        if (!_logger.IsEnabled(logLevel))
        {
            return;
        }

        LogCore(logLevel, default, message, exception, args);
    }

    /// <summary>
    /// Performs the is enabled operation.
    /// </summary>
    /// <param name="severity">The severity.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool IsEnabled(LogSeverity severity)
    {
        return _logger.IsEnabled(LogLevelMapper.Map(severity));
    }

    /// <summary>
    /// Performs the log event operation.
    /// </summary>
    /// <param name="eventId">The event id.</param>
    /// <param name="severity">The severity.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="args">The args.</param>
    public void LogEvent(
        int eventId,
        LogSeverity severity,
        string? message,
        Exception? exception,
        params object?[] args)
    {
        var logLevel = LogLevelMapper.Map(severity);
        if (_logger.IsEnabled(logLevel))
        {
            LogCore(logLevel, new EventId(eventId), message, exception, args);
        }
    }

    private void LogCore(
        LogLevel logLevel,
        EventId eventId,
        string? message,
        Exception? exception,
        object?[] args)
    {
        if (exception is null || _exceptionLoggingMode == ExceptionLoggingMode.FullDiagnostic)
        {
            _logger.Log(logLevel, eventId, exception, message, args);
            return;
        }

        var sanitized = _exceptionSanitizer.Sanitize(exception, _exceptionLoggingMode);
        var suffix = sanitized.Summary is null
            ? " Exception type: {0}."
            : " {0} Exception type: {1}.";
        var safeMessage = (message ?? string.Empty) + suffix;
        var safeArgs = sanitized.Summary is null
            ? args.Concat([sanitized.Type]).ToArray()
            : args.Concat([sanitized.Summary, sanitized.Type]).ToArray();
        _logger.Log(logLevel, eventId, null, safeMessage, safeArgs);
    }
}
