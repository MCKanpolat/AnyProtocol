using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol.Extensions;

/// <summary>
/// Provides extension methods for log writer configuration and registration.
/// </summary>
public static class LogWriterExtensions
{
    /// <summary>
    /// Performs the log trace operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogTrace(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Trace, message, null, args);
    }

    /// <summary>
    /// Performs the log trace operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogTrace(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Trace, message, exception, args);
    }

    /// <summary>
    /// Performs the log debug operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogDebug(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Debug, message, null, args);
    }

    /// <summary>
    /// Performs the log debug operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogDebug(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Debug, message, exception, args);
    }

    /// <summary>
    /// Performs the log info operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogInfo(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Info, message, null, args);
    }

    /// <summary>
    /// Performs the log info operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogInfo(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Info, message, exception, args);
    }

    /// <summary>
    /// Performs the log warning operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogWarning(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Warning, message, null, args);
    }

    /// <summary>
    /// Performs the log warning operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogWarning(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Warning, message, exception, args);
    }

    /// <summary>
    /// Performs the log error operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogError(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Error, message, null, args);
    }

    /// <summary>
    /// Performs the log error operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogError(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Error, message, exception, args);
    }

    /// <summary>
    /// Performs the log fatal operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogFatal(this ILogWriter logger, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Fatal, message, null, args);
    }

    /// <summary>
    /// Performs the log fatal operation.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="exception">The error that caused the operation to fail.</param>
    /// <param name="message">The message payload or description.</param>
    /// <param name="args">The args.</param>
    public static void LogFatal(this ILogWriter logger, Exception? exception, string? message, params object?[] args)
    {
        logger.Log(LogSeverity.Fatal, message, exception, args);
    }
}