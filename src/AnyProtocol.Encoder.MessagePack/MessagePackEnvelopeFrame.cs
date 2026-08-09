using MessagePack;

namespace AnyProtocol.Encoder.MessagePack;

/// <summary>
/// Defines the stable MessagePack envelope schema.
/// </summary>
[MessagePackObject]
public sealed class MessagePackEnvelopeFrame
{
    /// <summary>
    /// The current envelope schema version.
    /// </summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// Gets or sets the envelope schema version.
    /// </summary>
    [Key(0)]
    public byte Version { get; set; } = CurrentVersion;

    /// <summary>
    /// Gets or sets the envelope headers.
    /// </summary>
    [Key(1)]
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the opaque serialized application body.
    /// </summary>
    [Key(2)]
    public byte[] Body { get; set; } = [];
}
