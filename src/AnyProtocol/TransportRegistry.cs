using AnyProtocol.Protocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Stores and resolves transport registrations.
/// </summary>
public sealed class TransportRegistry
{
    private readonly IReadOnlyDictionary<ProtocolKey, IMessagingProtocol> _transports;

    /// <summary>
    /// Initializes a new instance of the TransportRegistry class.
    /// </summary>
    /// <param name="transports">The transports.</param>
    public TransportRegistry(IEnumerable<KeyValuePair<ProtocolKey, IMessagingProtocol>> transports)
    {
        _transports = new Dictionary<ProtocolKey, IMessagingProtocol>(transports);
    }

    /// <summary>
    /// Initializes a new instance of the TransportRegistry class.
    /// </summary>
    /// <param name="transports">The transports.</param>
    public TransportRegistry(IEnumerable<KeyValuePair<string, IMessagingProtocol>> transports)
        : this(transports.Select(pair =>
            new KeyValuePair<ProtocolKey, IMessagingProtocol>(
                ProtocolKey.Create(pair.Key),
                pair.Value)))
    {
    }

    /// <summary>
    /// Gets required.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <returns>The result of the get required operation.</returns>
    public IMessagingProtocol GetRequired(ProtocolKey protocol)
        => _transports.TryGetValue(protocol, out var transport)
            ? transport
            : throw new KeyNotFoundException($"Protocol '{protocol}' is not registered.");

    /// <summary>
    /// Gets required.
    /// </summary>
    /// <param name="name">The registered instance name.</param>
    /// <returns>The result of the get required operation.</returns>
    public IMessagingProtocol GetRequired(string name)
        => GetRequired(ProtocolKey.Create(name));

    /// <summary>
    /// Attempts to get.
    /// </summary>
    /// <param name="protocol">The protocol registration key.</param>
    /// <param name="transport">The transport.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool TryGet(ProtocolKey protocol, out IMessagingProtocol? transport)
        => _transports.TryGetValue(protocol, out transport);

    /// <summary>
    /// Gets the all.
    /// </summary>
    /// <value>The all.</value>
    public IEnumerable<IMessagingProtocol> All => _transports.Values.Distinct();

    /// <summary>
    /// Gets the entries.
    /// </summary>
    /// <value>The entries.</value>
    public IEnumerable<KeyValuePair<ProtocolKey, IMessagingProtocol>> Entries => _transports;
}
