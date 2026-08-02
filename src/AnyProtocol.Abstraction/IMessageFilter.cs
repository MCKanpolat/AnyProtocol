namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for message.
/// </summary>
public interface IMessageFilter
{
    /// <summary>
    /// Performs the invoke async operation.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next);
}
