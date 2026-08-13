namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Controls intake and disposal of one transport subscription independently.
/// </summary>
public interface ITransportSubscription : IAsyncDisposable
{
    /// <summary>
    /// Stops accepting new messages while allowing already accepted callbacks to finish.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask StopAcceptingAsync(CancellationToken cancellationToken = default);
}
