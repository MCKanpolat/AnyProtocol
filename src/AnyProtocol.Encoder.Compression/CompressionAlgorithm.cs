namespace AnyProtocol.Encoder.Compression;

/// <summary>
/// Identifies the compression algorithm used by a compressed envelope frame.
/// </summary>
public enum CompressionAlgorithm : byte
{
    /// <summary>
    /// GZip compression.
    /// </summary>
    GZip = 1,

    /// <summary>
    /// Brotli compression.
    /// </summary>
    Brotli = 2
}
