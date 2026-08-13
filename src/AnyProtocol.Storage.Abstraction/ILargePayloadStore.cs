namespace AnyProtocol.Storage.Abstraction;

/// <summary>Stores serialized message bodies outside a messaging transport.</summary>
public interface ILargePayloadStore
{
    /// <summary>Gets the logical provider registration name.</summary>
    string Name { get; }

    /// <summary>Stores a body using the message identifier as the idempotency key.</summary>
    ValueTask<StoredPayloadReference> StoreAsync(
        string messageId,
        ReadOnlyMemory<byte> payload,
        PayloadWriteOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a previously stored body.</summary>
    ValueTask<ReadOnlyMemory<byte>> LoadAsync(
        StoredPayloadReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a stored body for administrative cleanup.</summary>
    ValueTask DeleteAsync(
        StoredPayloadReference reference,
        CancellationToken cancellationToken = default);
}
