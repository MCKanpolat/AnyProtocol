using AnyProtocol.Abstraction;

namespace AnyProtocol.Encoder.Abstraction;

/// <summary>
/// Defines operations for message decoder.
/// </summary>
public interface IMessageDecoder
{
    /// <summary>
    /// Decodes a message value from transport data.
    /// </summary>
    /// <param name="frame">The frame to process.</param>
    /// <returns>The value produced by the operation.</returns>
    TransportEnvelope Decode(ReadOnlyMemory<byte> frame);
}