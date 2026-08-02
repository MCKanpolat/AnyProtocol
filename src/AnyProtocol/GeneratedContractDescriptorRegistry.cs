namespace AnyProtocol;

/// <summary>
/// Stores and resolves generated contract descriptor registrations.
/// </summary>
public static class GeneratedContractDescriptorRegistry
{
    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="factory">The factory used to create the instance.</param>
    public static void Register(Type contractType, Func<string, ContractDescriptor> factory)
        => GeneratedContractRegistry.RegisterDescriptor(contractType, factory);

    /// <summary>
    /// Performs the is registered operation.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool IsRegistered(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return GeneratedContractRegistry.IsDescriptorRegistered(contractType);
    }

    internal static bool TryCreate(
        Type contractType,
        string channelPrefix,
        out ContractDescriptor? descriptor)
        => GeneratedContractRegistry.TryCreateDescriptor(
            contractType,
            channelPrefix,
            out descriptor);
}
