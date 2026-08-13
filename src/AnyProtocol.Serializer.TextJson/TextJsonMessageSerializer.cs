using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AnyProtocol.Serializer.Abstraction;
using AnyProtocol.Serializer.Abstraction.Exceptions;

namespace AnyProtocol.Serializer.TextJson;

/// <summary>
/// Serializes and deserializes text json message message payloads.
/// </summary>
public sealed class TextJsonMessageSerializer : IMessageSerializer, IMessageSerializerMetadataProvider
{
    private readonly JsonSerializerOptions _options;

    /// <summary>
    /// Initializes a new instance of the TextJsonMessageSerializer class.
    /// </summary>
    public TextJsonMessageSerializer()
        : this(JsonSerializerOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TextJsonMessageSerializer class.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    public TextJsonMessageSerializer(JsonSerializerContext context)
        : this(context?.Options ?? throw new ArgumentNullException(nameof(context)))
    {
    }

    /// <summary>
    /// Initializes a new instance of the TextJsonMessageSerializer class.
    /// </summary>
    /// <param name="options">The options that control the operation.</param>
    public TextJsonMessageSerializer(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Performs the supports type operation.
    /// </summary>
    /// <param name="messageType">The runtime type to resolve.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public bool SupportsType(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        try
        {
            return _options.GetTypeInfo(messageType) is not null;
        }
        catch (NotSupportedException)
        {
            return false;
        }
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
            return JsonSerializer.SerializeToUtf8Bytes(
                data,
                (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)));
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to serialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Asynchronously serializes a message value into a UTF-8 JSON payload.
    /// </summary>
    /// <typeparam name="T">The message type to serialize.</typeparam>
    /// <param name="data">The data.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the UTF-8 JSON payload.</returns>
    public async ValueTask<ReadOnlyMemory<byte>> SerializeAsync<T>(T data, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(
                memoryStream,
                data,
                (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)),
                cancellationToken);

            return memoryStream.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            return JsonSerializer.SerializeToUtf8Bytes(data, _options.GetTypeInfo(type));
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
            await using var memoryStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(
                memoryStream,
                data,
                _options.GetTypeInfo(type),
                cancellationToken);

            return memoryStream.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            return JsonSerializer.Deserialize(
                bytes.Span,
                (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)));
        }
        catch (Exception ex)
        {
            throw new SerializationFailedException($"Failed to deserialize message of type {typeof(T).FullName}", ex);
        }
    }

    /// <summary>
    /// Asynchronously deserializes a message value from a UTF-8 JSON payload.
    /// </summary>
    /// <typeparam name="T">The message type to deserialize.</typeparam>
    /// <param name="bytes">The bytes.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the deserialized message.</returns>
    public async ValueTask<T?> DeserializeAsync<T>(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(
                new MemoryStream(bytes.ToArray()),
                (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
            return JsonSerializer.Deserialize(bytes.Span, _options.GetTypeInfo(type));
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
            return await JsonSerializer.DeserializeAsync(
                new MemoryStream(bytes.ToArray()),
                _options.GetTypeInfo(type),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
