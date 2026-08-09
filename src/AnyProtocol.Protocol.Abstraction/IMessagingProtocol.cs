using AnyProtocol.Abstraction;

namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Defines the common lifecycle and capability metadata for a messaging transport.
/// Operation-specific contracts are exposed through capability interfaces such as
/// <see cref="ISendTransport"/> and <see cref="ISubscriptionTransport"/>.
/// </summary>
public interface IMessagingProtocol : IAsyncDisposable
{
    /// <summary>
    /// Gets the capabilities.
    /// </summary>
    /// <value>The capabilities.</value>
    TransportCapabilities Capabilities { get; }

    /// <summary>
    /// Gets the transport's delivery, ordering, durability, and flow-control semantics.
    /// Existing transports receive conservative defaults derived from
    /// <see cref="Capabilities"/>.
    /// </summary>
    TransportSemantics Semantics => TransportSemantics.FromCapabilities(Capabilities);

}
