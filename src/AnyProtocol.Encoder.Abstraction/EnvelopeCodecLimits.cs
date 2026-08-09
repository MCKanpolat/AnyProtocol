namespace AnyProtocol.Encoder.Abstraction;

/// <summary>
/// Defines the common resource limits enforced by envelope codecs.
/// </summary>
public sealed record EnvelopeCodecLimits
{
    /// <summary>
    /// The default maximum complete frame size.
    /// </summary>
    public const int DefaultMaxFrameSize = 64 * 1024 * 1024;

    /// <summary>
    /// The default maximum envelope body size.
    /// </summary>
    public const int DefaultMaxBodySize = 64 * 1024 * 1024;

    /// <summary>
    /// The default maximum number of headers.
    /// </summary>
    public const int DefaultMaxHeaderCount = 1024;

    /// <summary>
    /// The default maximum aggregate UTF-8 header bytes.
    /// </summary>
    public const int DefaultMaxHeaderBytes = 1024 * 1024;

    /// <summary>
    /// The default maximum decompressed frame size.
    /// </summary>
    public const int DefaultMaxDecompressedSize = 64 * 1024 * 1024;

    /// <summary>
    /// Gets the default limits.
    /// </summary>
    public static EnvelopeCodecLimits Default { get; } = new();

    /// <summary>
    /// Gets the maximum complete frame size.
    /// </summary>
    public int MaxFrameSize { get; init; } = DefaultMaxFrameSize;

    /// <summary>
    /// Gets the maximum envelope body size.
    /// </summary>
    public int MaxBodySize { get; init; } = DefaultMaxBodySize;

    /// <summary>
    /// Gets the maximum number of headers.
    /// </summary>
    public int MaxHeaderCount { get; init; } = DefaultMaxHeaderCount;

    /// <summary>
    /// Gets the maximum aggregate UTF-8 header bytes.
    /// </summary>
    public int MaxHeaderBytes { get; init; } = DefaultMaxHeaderBytes;

    /// <summary>
    /// Gets the maximum decompressed frame size used by compression decorators.
    /// </summary>
    public int MaxDecompressedSize { get; init; } = DefaultMaxDecompressedSize;

    /// <summary>
    /// Validates the configured limits.
    /// </summary>
    public void Validate()
    {
        if (MaxFrameSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxFrameSize), MaxFrameSize, "MaxFrameSize must be greater than zero.");
        }

        if (MaxBodySize < 0 || MaxBodySize > MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxBodySize), MaxBodySize, "MaxBodySize must be non-negative and no greater than MaxFrameSize.");
        }

        if (MaxHeaderCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHeaderCount), MaxHeaderCount, "MaxHeaderCount must be non-negative.");
        }

        if (MaxHeaderBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHeaderBytes), MaxHeaderBytes, "MaxHeaderBytes must be non-negative.");
        }

        if (MaxDecompressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDecompressedSize), MaxDecompressedSize, "MaxDecompressedSize must be non-negative.");
        }
    }
}
