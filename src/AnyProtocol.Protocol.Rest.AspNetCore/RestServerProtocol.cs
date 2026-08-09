using AnyProtocol.Abstraction;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Protocol.Rest.AspNetCore;

/// <summary>
/// Implements rest server messaging transport operations.
/// </summary>
public sealed class RestServerProtocol :
    IMessagingProtocol,
    INativeServerTransport,
    ITransportReadiness
{
    private int _mapped;
    /// <summary>
    /// Gets the optional transport capabilities supported by this protocol.
    /// </summary>
    /// <value>The capabilities.</value>
    public TransportCapabilities Capabilities =>
        TransportCapabilities.NativeHeaders |
        TransportCapabilities.NativeRequestReply;

    /// <summary>
    /// Gets the delivery and ordering guarantees provided by this protocol.
    /// </summary>
    /// <value>The semantics.</value>
    public TransportSemantics Semantics { get; } = new()
    {
        DeliveryGuarantee = TransportDeliveryGuarantee.AtMostOnce,
        Ordering = TransportOrdering.None,
        Durability = TransportDurability.Volatile,
        SupportsNativeRequestReply = true,
        SupportsBackpressure = true,
        SupportsCancellation = true
    };

    /// <summary>
    /// Asynchronously releases resources owned by this instance.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Checks whether the transport is ready to handle messages without mutating application state.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the check readiness async.</returns>
    public ValueTask<TransportReadinessResult> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            Volatile.Read(ref _mapped) != 0
                ? TransportReadinessResult.Ready("The REST endpoints are mapped.")
                : TransportReadinessResult.NotReady("The REST endpoints are not mapped."));
    }

    internal void MarkMapped() => Volatile.Write(ref _mapped, 1);
}
