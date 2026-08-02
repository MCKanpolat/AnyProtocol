namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Identifies the available transport readiness values.
/// </summary>
public enum TransportReadinessState
{
    /// <summary>
    /// Indicates that readiness has not yet been determined.
    /// </summary>
    Unknown,
    /// <summary>
    /// Indicates that the transport is ready to handle messages.
    /// </summary>
    Ready,
    /// <summary>
    /// Indicates that the transport is not ready to handle messages.
    /// </summary>
    NotReady
}

/// <summary>
/// Contains the outcome of transport readiness processing.
/// </summary>
/// <param name="State">The state.</param>
/// <param name="Description">An optional human-readable description.</param>
public readonly record struct TransportReadinessResult(
    TransportReadinessState State,
    string? Description = null)
{
    /// <summary>
    /// Creates a readiness result indicating that the transport is ready.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <returns>A ready transport result with the optional description.</returns>
    public static TransportReadinessResult Ready(string? description = null)
        => new(TransportReadinessState.Ready, description);

    /// <summary>
    /// Creates a readiness result indicating that the transport is not ready.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <returns>A not-ready transport result with the optional description.</returns>
    public static TransportReadinessResult NotReady(string? description = null)
        => new(TransportReadinessState.NotReady, description);

    /// <summary>
    /// Creates a readiness result whose state has not yet been determined.
    /// </summary>
    /// <param name="description">An optional human-readable description.</param>
    /// <returns>An unknown transport result with the optional description.</returns>
    public static TransportReadinessResult Unknown(string? description = null)
        => new(TransportReadinessState.Unknown, description);
}

/// <summary>
/// Provides a non-destructive readiness assessment for a messaging transport.
/// Implementations must not publish, consume, or mutate application messages.
/// </summary>
public interface ITransportReadiness
{
    /// <summary>
    /// Checks whether the transport is ready without publishing, consuming, or mutating application messages.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result describes the current transport readiness.</returns>
    ValueTask<TransportReadinessResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default);
}
