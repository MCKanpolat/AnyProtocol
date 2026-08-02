namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Identifies the available log severity values.
/// </summary>
public enum LogSeverity
{
    /// <summary>
    /// Indicates trace.
    /// </summary>
    Trace,
    /// <summary>
    /// Indicates debug.
    /// </summary>
    Debug,
    /// <summary>
    /// Indicates info.
    /// </summary>
    Info,
    /// <summary>
    /// Indicates warning.
    /// </summary>
    Warning,
    /// <summary>
    /// Indicates error.
    /// </summary>
    Error,
    /// <summary>
    /// Indicates fatal.
    /// </summary>
    Fatal
}