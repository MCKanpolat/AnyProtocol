namespace AnyProtocol.Serializer.Abstraction;

/// <summary>
/// Defines operations for message.
/// </summary>
public interface IMessageSerializer : IDisposable
{
    /// <summary>
    /// Serializes a message value into a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to serialize.</typeparam>
    /// <param name="data">The data.</param>
    /// <returns>The value produced by the operation.</returns>
    ReadOnlyMemory<byte> Serialize<T>(T data);

    /// <summary>
    /// Serializes a message value into a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to serialize.</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<ReadOnlyMemory<byte>> SerializeAsync<T>(T data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes a message value into a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="data">The data.</param>
    /// <returns>The value produced by the operation.</returns>
    ReadOnlyMemory<byte> Serialize(Type type, object data);

    /// <summary>
    /// Serializes a message value into a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<ReadOnlyMemory<byte>> SerializeAsync(Type type, object data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes a message value from a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to deserialize.</typeparam>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The value produced by the operation.</returns>
    T? Deserialize<T>(ReadOnlyMemory<byte> bytes);

    /// <summary>
    /// Deserializes a message value from a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to deserialize.</typeparam>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<T?> DeserializeAsync<T>(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deserializes a message value from a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The value produced by the operation.</returns>
    object? Deserialize(Type type, ReadOnlyMemory<byte> bytes);

    /// <summary>
    /// Deserializes a message value from a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<object?> DeserializeAsync(Type type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
}

/// <summary>
/// Defines operations for message serializer metadata.
/// </summary>
public interface IMessageSerializerMetadataProvider
{
    /// <summary>
    /// Performs the supports type operation.
    /// </summary>
    /// <param name="messageType">The runtime type to resolve.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    bool SupportsType(Type messageType);
}