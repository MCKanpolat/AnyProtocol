namespace AnyProtocol.Abstraction;

/// <summary>
/// Identifies the available contract operation values.
/// </summary>
public enum ContractOperation
{
    /// <summary>
    /// Indicates request.
    /// </summary>
    Request = 1,
    /// <summary>
    /// Indicates send.
    /// </summary>
    Send = 2,
    /// <summary>
    /// Indicates stream.
    /// </summary>
    Stream = 3
}
