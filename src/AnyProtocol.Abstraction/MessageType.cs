namespace AnyProtocol.Abstraction;

/// <summary>
/// Identifies the available message type values.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// Indicates request.
    /// </summary>
    Request = 1,
    /// <summary>
    /// Indicates response.
    /// </summary>
    Response = 2,
    /// <summary>
    /// Indicates event.
    /// </summary>
    Event = 3,
    /// <summary>
    /// Indicates stream item.
    /// </summary>
    StreamItem = 4,
    /// <summary>
    /// Indicates stream complete.
    /// </summary>
    StreamComplete = 5,
    /// <summary>
    /// Indicates fault.
    /// </summary>
    Fault = 6
}
