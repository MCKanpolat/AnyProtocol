using AnyProtocol.Storage.Abstraction;

namespace AnyProtocol;

/// <summary>Resolves payload stores by their case-insensitive logical names.</summary>
public sealed class LargePayloadStoreRegistry
{
    private readonly IReadOnlyDictionary<string, ILargePayloadStore> _stores;

    /// <summary>Creates a registry and rejects duplicate or empty logical names.</summary>
    public LargePayloadStoreRegistry(IEnumerable<ILargePayloadStore> stores)
    {
        ArgumentNullException.ThrowIfNull(stores);
        var configuredStores = stores.ToArray();
        if (configuredStores.Any(static store => store is null))
        {
            throw new InvalidOperationException("A large-payload store registration cannot be null.");
        }
        if (configuredStores.Any(static store => string.IsNullOrWhiteSpace(store.Name)))
        {
            throw new InvalidOperationException("Every large-payload store must have a name.");
        }

        try
        {
            _stores = configuredStores.ToDictionary(
                static store => store.Name,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "More than one large-payload store uses the same logical name.",
                exception);
        }

    }

    /// <summary>Gets the named store or throws a stable configuration failure.</summary>
    public ILargePayloadStore GetRequired(string name)
        => _stores.TryGetValue(name, out var store)
            ? store
            : throw new LargePayloadException(
                LargePayloadFailureCodes.StoreNotConfigured,
                $"Large-payload store '{name}' is not configured.");

    /// <summary>Returns whether a store is registered under the logical name.</summary>
    public bool Contains(string name) => _stores.ContainsKey(name);
}
