namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for anyprotocol bus.
/// </summary>
public interface IAnyProtocolBus
{
    /// <summary>
    /// Gets the is started.
    /// </summary>
    /// <value>true when is started applies; otherwise, false.</value>
    bool IsStarted { get; }

    /// <summary>
    /// Indicates state.
    /// </summary>
    /// <value>The state.</value>
    AnyProtocolBusState State =>
        IsStarted ? AnyProtocolBusState.Started : AnyProtocolBusState.Stopped;

    /// <summary>
    /// Performs the start async operation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the stop async operation.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}