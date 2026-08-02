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
    /// Indicates publish subscribe.
    /// </summary>
    PublishSubscribe = 1 << 0,
    /// <summary>
    /// Indicates competing consumers.
    /// </summary>
    CompetingConsumers = 1 << 1,
    /// <summary>
    /// Indicates native headers.
    /// </summary>
    NativeHeaders = 1 << 2,
    /// <summary>
    /// Indicates native request reply.
    /// </summary>
    NativeRequestReply = 1 << 3,
    /// <summary>
    /// Indicates native streaming.
    /// </summary>
    NativeStreaming = 1 << 4
}
