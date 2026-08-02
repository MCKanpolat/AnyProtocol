namespace AnyProtocol.Abstraction;

/// <summary>
/// Represents a successful operation that has no response payload.
/// </summary>
public readonly record struct Unit
{
    /// <summary>
    /// Gets the value represented by this member.
    /// </summary>
    /// <value>The value.</value>
    public static Unit Value => default;
}

/// <summary>
/// Represents a request that carries no application payload.
/// </summary>
public sealed record EmptyRequest
{
    /// <summary>
    /// Gets the shared instance.
    /// </summary>
    /// <value>The instance.</value>
    public static EmptyRequest Instance { get; } = new();
}
