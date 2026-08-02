namespace AnyProtocol.Abstraction;

/// <summary>
/// Marks a declaration with channel metadata.
/// </summary>
/// <param name="name">The registered instance name.</param>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
public sealed class ChannelAttribute(string name) : Attribute
{
    /// <summary>
    /// Gets the name.
    /// </summary>
    /// <value>The name.</value>
    public string Name { get; } = name;
}

/// <summary>
/// Marks a declaration with expect reply metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExpectReplyAttribute : Attribute;

/// <summary>
/// Marks an operation as safe to invoke more than once, enabling automatic retries.
/// </summary>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute;

/// <summary>
/// Marks a declaration with require permission metadata.
/// </summary>
/// <param name="permission">The permission.</param>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : Attribute
{
    /// <summary>
    /// Gets the permission.
    /// </summary>
    /// <value>The permission.</value>
    public string Permission { get; } = permission;
}

/// <summary>
/// Marks a declaration with fault contract metadata.
/// </summary>
/// <param name="faultType">The fault type.</param>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
public sealed class FaultContractAttribute(Type faultType) : Attribute
{
    /// <summary>
    /// Gets the fault type.
    /// </summary>
    /// <value>The fault type.</value>
    public Type FaultType { get; } = faultType;
}

/// <summary>
/// Marks a declaration with partition key metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PartitionKeyAttribute : Attribute;

/// <summary>
/// Marks a declaration with mcp tool metadata.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolAttribute : Attribute
{
    /// <summary>
    /// Gets or initializes the name.
    /// </summary>
    /// <value>The name.</value>
    public string? Name { get; init; }

    /// <summary>
    /// Gets or initializes the description.
    /// </summary>
    /// <value>The description.</value>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether read only applies.
    /// </summary>
    /// <value>true when read only applies; otherwise, false.</value>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether destructive applies.
    /// </summary>
    /// <value>true when destructive applies; otherwise, false.</value>
    public bool Destructive { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether idempotent applies.
    /// </summary>
    /// <value>true when idempotent applies; otherwise, false.</value>
    public bool Idempotent { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether open world applies.
    /// </summary>
    /// <value>true when open world applies; otherwise, false.</value>
    public bool OpenWorld { get; init; }
}
