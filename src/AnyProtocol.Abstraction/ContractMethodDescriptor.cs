using System.Reflection;

namespace AnyProtocol.Abstraction;

/// <summary>
/// Describes contract method metadata used at runtime.
/// </summary>
public sealed class ContractMethodDescriptor
{
    /// <summary>
    /// Gets or initializes the contract type.
    /// </summary>
    /// <value>The contract type.</value>
    public required Type ContractType { get; init; }

    /// <summary>
    /// Gets or initializes the method.
    /// </summary>
    /// <value>The method.</value>
    public required MethodInfo Method { get; init; }

    /// <summary>
    /// Gets or initializes the contract name.
    /// </summary>
    /// <value>The contract name.</value>
    public required string ContractName { get; init; }

    /// <summary>
    /// Gets or initializes the method name.
    /// </summary>
    /// <value>The method name.</value>
    public required string MethodName { get; init; }

    /// <summary>
    /// Gets or initializes the channel.
    /// </summary>
    /// <value>The channel.</value>
    public required string Channel { get; init; }

    /// <summary>
    /// Gets or initializes the request type.
    /// </summary>
    /// <value>The request type.</value>
    public required Type RequestType { get; init; }

    /// <summary>
    /// Gets or initializes the response type.
    /// </summary>
    /// <value>The response type.</value>
    public Type? ResponseType { get; init; }

    /// <summary>
    /// Gets or initializes the operation.
    /// </summary>
    /// <value>The operation.</value>
    public required ContractOperation Operation { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether has cancellation token applies.
    /// </summary>
    /// <value>true when has cancellation token applies; otherwise, false.</value>
    public bool HasCancellationToken { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether expect reply applies.
    /// </summary>
    /// <value>true when expect reply applies; otherwise, false.</value>
    public bool ExpectReply { get; init; }

    /// <summary>
    /// Gets or initializes a value indicating whether is idempotent applies.
    /// </summary>
    /// <value>true when is idempotent applies; otherwise, false.</value>
    public bool IsIdempotent { get; init; }

    /// <summary>
    /// Gets or initializes the required permissions.
    /// </summary>
    /// <value>The required permissions.</value>
    public IReadOnlyList<string> RequiredPermissions { get; init; } = [];

    /// <summary>
    /// Gets or initializes the fault type.
    /// </summary>
    /// <value>The fault type.</value>
    public Type? FaultType { get; init; }

    /// <summary>
    /// Gets or initializes the partition key property.
    /// </summary>
    /// <value>The partition key property.</value>
    public PropertyInfo? PartitionKeyProperty { get; init; }
}
