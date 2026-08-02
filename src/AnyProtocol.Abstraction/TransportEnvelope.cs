namespace AnyProtocol.Abstraction;

/// <summary>
/// Contains a serialized payload and its transport-level metadata.
/// </summary>
/// <param name="Headers">The headers.</param>
/// <param name="Body">The body.</param>
public sealed record TransportEnvelope(IMessageHeaders Headers, ReadOnlyMemory<byte> Body);
