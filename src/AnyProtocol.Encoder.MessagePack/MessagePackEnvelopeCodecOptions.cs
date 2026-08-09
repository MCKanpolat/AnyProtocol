using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol.Encoder.MessagePack;

/// <summary>
/// Configures the MessagePack envelope codec.
/// </summary>
public sealed record MessagePackEnvelopeCodecOptions
{
    /// <summary>
    /// Gets the default options.
    /// </summary>
    public static MessagePackEnvelopeCodecOptions Default { get; } = new();

    /// <summary>
    /// Gets the common envelope limits used by the codec.
    /// </summary>
    public EnvelopeCodecLimits Limits { get; init; } = EnvelopeCodecLimits.Default;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Limits);
        Limits.Validate();
    }
}
