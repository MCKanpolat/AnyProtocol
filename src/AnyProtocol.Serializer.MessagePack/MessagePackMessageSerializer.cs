using System.Buffers;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.IO;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Serializer.Abstraction.Exceptions;

namespace AnyProtocol.Serializer.MessagePack;

/// <summary>
/// Serializes and deserializes message pack message message payloads.
/// </summary>
public sealed class MessagePackMessageSerializer : IMessageSerializer
{
    private readonly RecyclableMemoryStreamManager _streamManager;
    const int BlockSize = 1024;
    const int LargeBufferMultiple = 1024 * 1024;
    const int MaxBufferSize = 16 * LargeBufferMultiple;

    /// <summary>
    /// Initializes a new instance of the MessagePackMessageSerializer class.
    /// </summary>
    public MessagePackMessageSerializer()
    {
        _streamManager =  _streamManager = new RecyclableMemoryStreamManager(
            new RecyclableMemoryStreamManager.Options(BlockSize, LargeBufferMultiple, MaxBufferSize,
                                                      100 * BlockSize, MaxBufferSize * 4) { GenerateCallStacks = false, AggressiveBufferReturn = false });

    }

    /// <summary>
    /// Serializes &lt;t&gt; into a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to serialize.</typeparam>
    /// <param name="data">The data.</param>
    /// <returns>The result of the serialize&lt;t&gt; operation.</returns>
    public ReadOnlyMemory<byte> Serialize<T>(T data)
    {
        try
        {
            return MessagePackSerializer.Serialize(data, ContractlessStandardResolver.Options);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to serialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Asynchronously serializes a message value into a MessagePack payload.
    /// </summary>
    /// <typeparam name="T">The message type to serialize.</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the MessagePack payload.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> SerializeAsync<T>(T data, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var memoryStream = _streamManager.GetStream(typeof(T).Name);
            await MessagePackSerializer.SerializeAsync(memoryStream, data, ContractlessStandardResolver.Options, cancellationToken);

            return memoryStream.GetReadOnlySequence()
                .ToArray();
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to serialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Serializes  into a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="data">The data.</param>
    /// <returns>The result of the serialize operation.</returns>
    public ReadOnlyMemory<byte> Serialize(Type type, object data)
    {
        try
        {
            return MessagePackSerializer.Serialize(type, data, ContractlessStandardResolver.Options);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to serialize message of type {type.FullName}", ex);
        }
    }

    /// <summary>
    /// Serializes async into a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the serialize async.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> SerializeAsync(Type type, object data, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var memoryStream = _streamManager.GetStream(type.Name) as RecyclableMemoryStream;
            await MessagePackSerializer.SerializeAsync(memoryStream, data, ContractlessStandardResolver.Options, cancellationToken);

            return memoryStream.GetReadOnlySequence()
                .ToArray();
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to serialize message of type {type.FullName}", ex);
        }
    }

    /// <summary>
    /// Deserializes &lt;t&gt; from a transport payload.
    /// </summary>
    /// <typeparam name="T">The message type to deserialize.</typeparam>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The result of the deserialize&lt;t&gt; operation.</returns>
    public T? Deserialize<T>(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return MessagePackSerializer.Deserialize<T>(bytes, ContractlessStandardResolver.Options);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to deserialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Asynchronously deserializes a message value from a MessagePack payload.
    /// </summary>
    /// <typeparam name="T">The message type to deserialize.</typeparam>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the deserialized message.</returns>
    public async ValueTask<T?> DeserializeAsync<T>(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var memoryStream = _streamManager.GetStream(typeof(T).Name, bytes.Length) as RecyclableMemoryStream;
            await memoryStream.WriteAsync(bytes, cancellationToken);
            memoryStream.Seek(0, SeekOrigin.Begin);

            return await MessagePackSerializer.DeserializeAsync<T>(memoryStream, ContractlessStandardResolver.Options, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to deserialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Deserializes  from a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The result of the deserialize operation.</returns>
    public object? Deserialize(Type type, ReadOnlyMemory<byte> bytes)
    {
        try
        {
            return MessagePackSerializer.Deserialize(type, bytes, ContractlessStandardResolver.Options);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to deserialize message of type {type.FullName}", ex);
        }
    }

    /// <summary>
    /// Deserializes async from a transport payload.
    /// </summary>
    /// <param name="type">The runtime type to resolve.</param>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the deserialize async.</returns>
    public async ValueTask<object?> DeserializeAsync(Type type, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var memoryStream = _streamManager.GetStream(type.Name, bytes.Length) as RecyclableMemoryStream;
            await memoryStream.WriteAsync(bytes, cancellationToken);
            memoryStream.Seek(0, SeekOrigin.Begin);

            return await MessagePackSerializer.DeserializeAsync(type, memoryStream, ContractlessStandardResolver.Options, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to deserialize message of type {type.FullName}", ex);
        }
    }

    /// <summary>
    /// Releases resources owned by this instance.
    /// </summary>
    public void Dispose()
    {
        //cleanup
    }
}