using System.Collections.Concurrent;
using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol.Configuration;

/// <summary>Describes how a projection protocol is hosted by an adapter.</summary>
public interface IProtocolExposureDeclaration
{
    /// <summary>
    /// Gets the protocol.
    /// </summary>
    /// <value>The protocol.</value>
    ProtocolKey Protocol { get; }

    /// <summary>
    /// Gets the requires endpoint mapping.
    /// </summary>
    /// <value>true when requires endpoint mapping applies; otherwise, false.</value>
    bool RequiresEndpointMapping { get; }
}

/// <summary>Tracks endpoint mappings completed while the application is being built.</summary>
public sealed class ProtocolExposureRegistry
{
    private readonly ConcurrentDictionary<ProtocolKey, byte> _mappedProtocols = new();

    /// <summary>
    /// Performs the mark mapped operation.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    public void MarkMapped(ProtocolKey protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol.Value))
        {
            throw new ArgumentException("A protocol key is required.", nameof(protocol));
        }

        _mappedProtocols.TryAdd(protocol, 0);
    }

    /// <summary>
    /// Performs the is mapped operation.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool IsMapped(ProtocolKey protocol) => _mappedProtocols.ContainsKey(protocol);
}
