using AnyProtocol.Serializer.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Stores and resolves serializer registrations.
/// </summary>
public sealed class SerializerRegistry
{
    private readonly Dictionary<string, IMessageSerializer> _serializers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contentType">The content type.</param>
    /// <param name="serializer">The serializer.</param>
    /// <returns>The result of the register operation.</returns>
    public SerializerRegistry Register(string contentType, IMessageSerializer serializer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(serializer);
        _serializers[contentType] = serializer;
        return this;
    }

    /// <summary>
    /// Gets required.
    /// </summary>
    /// <param name="contentType">The content type.</param>
    /// <returns>The result of the get required operation.</returns>
    public IMessageSerializer GetRequired(string contentType)
    {
        if (_serializers.TryGetValue(contentType, out var serializer))
        {
            return serializer;
        }

        throw new KeyNotFoundException($"No serializer is registered for content type '{contentType}'.");
    }
}
