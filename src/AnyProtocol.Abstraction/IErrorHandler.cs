namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for error handler.
/// </summary>
public interface IErrorHandler
{
    /// <summary>
    /// Handles async.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask HandleAsync(IMessageContext context);
}
