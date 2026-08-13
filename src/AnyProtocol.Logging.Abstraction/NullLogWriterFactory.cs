namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Provides a safe no-op logging implementation for applications without a logger.
/// </summary>
public sealed class NullLogWriterFactory : ILogWriterFactory
{
    /// <summary>
    /// Gets the shared no-op factory instance.
    /// </summary>
    public static NullLogWriterFactory Instance { get; } = new();

    private NullLogWriterFactory()
    {
    }

    /// <inheritdoc />
    public ILogWriter CreateLogWriter(string categoryName) => NullLogWriter.Instance;

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class NullLogWriter : ILogWriter
    {
        public static NullLogWriter Instance { get; } = new();

        public void Log(
            LogSeverity severity,
            string? message,
            Exception? exception,
            params object?[] args)
        {
        }

        public bool IsEnabled(LogSeverity severity) => false;
    }
}
