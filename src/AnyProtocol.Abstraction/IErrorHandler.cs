namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for error handler.
/// </summary>
public interface IErrorHandler
{
    /// <summary>
    /// Handles async.
    /// </summary>
    /// <param name="context">The error context for the failed operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask HandleAsync(ErrorContext context);
}
