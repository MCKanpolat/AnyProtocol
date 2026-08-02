using Microsoft.Extensions.Logging;
using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol.Logging.Microsoft;

/// <summary>
/// Provides the microsoft log writer implementation used by AnyProtocol applications.
/// </summary>
public class MicrosoftLogWriter : ILogWriter
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the MicrosoftLogWriter class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public MicrosoftLogWriter(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

        _logger.Log(logLevel, exception, message, args);
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
            _logger.Log(logLevel, new EventId(eventId), exception, message, args);
        }
    }
}