using AnyProtocol.Encoder.Abstraction;

namespace AnyProtocol.Encoder.Compression;

/// <summary>
/// Configures the compressed envelope codec.
/// </summary>
public sealed record CompressedEnvelopeCodecOptions
{
    /// <summary>
    /// Gets or initializes the algorithm used for eligible frames.
    /// </summary>
    public CompressionAlgorithm Algorithm { get; init; } = CompressionAlgorithm.GZip;

    /// <summary>
    /// Gets or initializes the minimum encoded inner-frame size that is compressed.
    /// Smaller frames use the explicit uncompressed wrapper path.
    /// </summary>
    public int CompressionThreshold { get; init; } = 256;

    /// <summary>
    /// Gets or initializes the common envelope codec limits.
    /// </summary>
    /// <remarks>
    /// The compression decorator uses <see cref="EnvelopeCodecLimits.MaxFrameSize"/>
    /// for the complete wrapper and <see cref="EnvelopeCodecLimits.MaxDecompressedSize"/>
    /// for the complete inner frame. The inner codec should receive the same limits
    /// when it exposes configurable limits of its own.
    /// </remarks>
    public EnvelopeCodecLimits Limits { get; init; } = EnvelopeCodecLimits.Default;

    /// <summary>
    /// Gets or initializes the maximum allowed original-to-compressed size ratio.
    /// </summary>
    public double MaximumCompressionRatio { get; init; } = 100;

    /// <summary>
    /// Gets or initializes a value indicating whether a compressed wrapper may wrap
    /// another compressed wrapper.
    /// </summary>
    public bool AllowDoubleCompression { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Algorithm))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Algorithm),
                Algorithm,
                "The compression algorithm is not supported.");
        }

        if (CompressionThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompressionThreshold),
                CompressionThreshold,
                "The compression threshold must be non-negative.");
        }

        ArgumentNullException.ThrowIfNull(Limits);
        Limits.Validate();

        if (!double.IsFinite(MaximumCompressionRatio) || MaximumCompressionRatio < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumCompressionRatio),
                MaximumCompressionRatio,
                "The maximum compression ratio must be finite and at least one.");
        }
    }
}
