namespace AnyProtocol.Logging.Abstraction;

/// <summary>
/// Defines operations for log writer.
/// </summary>
public interface ILogWriterFactory: IDisposable
{
    /// <summary>
    /// Creates log writer.
    /// </summary>
    /// <param name="categoryName">The category name.</param>
    /// <returns>The value produced by the operation.</returns>
    ILogWriter CreateLogWriter(string categoryName);
}