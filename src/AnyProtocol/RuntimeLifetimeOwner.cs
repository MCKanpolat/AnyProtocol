namespace AnyProtocol;

/// <summary>
/// Owns outbound runtime resources that must stop before transports are disposed.
/// </summary>
internal sealed class RuntimeLifetimeOwner(
    AnyProtocolClientInvoker clientInvoker,
    OutboundOperationLifetime outboundLifetime) : IAsyncDisposable
{
    /// <summary>
    /// Releases outbound runtime resources before their transports are disposed.
    /// </summary>
    /// <returns>A task that represents the asynchronous disposal operation.</returns>
    public async ValueTask DisposeAsync()
    {
        outboundLifetime.CancelRun();

        try
        {
            await clientInvoker.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            outboundLifetime.Dispose();
        }
    }
}
