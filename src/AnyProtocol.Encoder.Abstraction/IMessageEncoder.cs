using AnyProtocol.Abstraction;

namespace AnyProtocol.Encoder.Abstraction;

/// <summary>
/// Defines operations for message encoder.
/// </summary>
public interface IMessageEncoder
{
    /// <summary>
    /// Encodes a message value for transport.
    /// </summary>
    /// <param name="envelope">The transport envelope to process.</param>
    /// <returns>The value produced by the operation.</returns>
    ReadOnlyMemory<byte> Encode(TransportEnvelope envelope);
}