using Microsoft.Extensions.Logging;
using AnyProtocol.Logging.Abstraction;

namespace AnyProtocol.Logging.Microsoft;

/// <summary>
/// Creates microsoft log writer instances.
/// </summary>
public sealed class MicrosoftLogWriterFactory : ILogWriterFactory
{
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the MicrosoftLogWriterFactory class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    public MicrosoftLogWriterFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Releases resources owned by this instance.
    /// </summary>
    public void Dispose()
    {
        _loggerFactory.Dispose();
    }

    /// <summary>
    /// Creates log writer.
    /// </summary>
    /// <param name="categoryName">The category name.</param>
    /// <returns>The result of the create log writer operation.</returns>
    public ILogWriter CreateLogWriter(string categoryName)
    {
        var logger = _loggerFactory.CreateLogger(categoryName);

        return new MicrosoftLogWriter(logger);
    }
}