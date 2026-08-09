namespace AnyProtocol.Encoder.Abstraction;

/// <summary>
/// Defines a paired encoder and decoder for transport envelopes.
/// </summary>
public interface IEnvelopeCodec : IMessageEncoder, IMessageDecoder
{
}
