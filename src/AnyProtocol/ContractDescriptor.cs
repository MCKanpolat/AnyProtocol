using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Describes contract metadata used at runtime.
/// </summary>
public sealed class ContractDescriptor
{
    /// <summary>
    /// Initializes a new instance of the ContractDescriptor class.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="methods">The methods.</param>
    public ContractDescriptor(Type contractType, IReadOnlyList<ContractMethodDescriptor> methods)
    {
        ContractType = contractType;
        Methods = methods;
    }

    /// <summary>
    /// Gets the contract type.
    /// </summary>
    /// <value>The contract type.</value>
    public Type ContractType { get; }

    /// <summary>
    /// Gets the methods.
    /// </summary>
    /// <value>The methods.</value>
    public IReadOnlyList<ContractMethodDescriptor> Methods { get; }
}
