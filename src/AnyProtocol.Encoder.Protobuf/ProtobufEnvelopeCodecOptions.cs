using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol.Encoder.Protobuf;

/// <summary>
/// Configures the Protobuf envelope codec.
/// </summary>
public sealed record ProtobufEnvelopeCodecOptions
{
    /// <summary>
    /// Gets or initializes the common envelope codec limits.
    /// </summary>
    public EnvelopeCodecLimits Limits { get; init; } = EnvelopeCodecLimits.Default;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        Limits.Validate();
    }
}
