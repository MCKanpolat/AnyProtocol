namespace AnyProtocol.Configuration;

/// <summary>Configures optional serialized-body offload.</summary>
public sealed class LargePayloadOffloadOptions
{
    /// <summary>Gets or sets the logical payload-store name.</summary>
    public string? StoreName { get; set; }

    /// <summary>Gets or sets the largest body kept inline.</summary>
    public int MaxInlinePayloadBytes { get; set; }

    /// <summary>Gets or sets the largest body accepted by the configured store.</summary>
    public int MaxStoredPayloadBytes { get; set; }

    /// <summary>Gets or sets how long stored bodies remain available.</summary>
    public TimeSpan TimeToLive { get; set; }
}

/// <summary>An immutable, validated large-payload policy.</summary>
public sealed record LargePayloadOffloadPolicy(
    string StoreName,
    int MaxInlinePayloadBytes,
    int MaxStoredPayloadBytes,
    TimeSpan TimeToLive);
