namespace AnyProtocol.Protocol.Abstraction;

/// <summary>
/// Identifies optional capabilities supported by a messaging transport.
/// </summary>
[Flags]
public enum TransportCapabilities
{
    /// <summary>
    /// Indicates none.
    /// </summary>
    None = 0,
    /// <summary>
    /// Indicates competing consumers.
    /// </summary>
    CompetingConsumers = 1 << 0,
    /// <summary>
    /// Indicates native headers.
    /// </summary>
    NativeHeaders = 1 << 1
}
