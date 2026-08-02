using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the timeout pipeline stage.
/// </summary>
/// <param name="timeout">The maximum time allowed for the operation.</param>
public sealed class TimeoutFilter(TimeSpan timeout) : IMessageFilter
{
    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var originalToken = context.CancellationToken;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(originalToken);
        timeoutSource.CancelAfter(timeout);
        context.CancellationToken = timeoutSource.Token;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!originalToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Message '{context.Channel}' exceeded timeout '{timeout}'.");
        }
        finally
        {
            context.CancellationToken = originalToken;
        }
    }
}
