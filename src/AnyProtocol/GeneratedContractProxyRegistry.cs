using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Stores and resolves generated contract proxy registrations.
/// </summary>
public static class GeneratedContractProxyRegistry
{
    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="factory">The factory used to create the instance.</param>
    public static void Register(
        Type contractType,
        Func<IClientInvoker, ContractDescriptorFactory, object> factory)
        => GeneratedContractRegistry.RegisterProxy(contractType, factory);

    /// <summary>
    /// Performs the is registered operation.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool IsRegistered(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return GeneratedContractRegistry.IsProxyRegistered(contractType);
    }

    internal static bool TryCreate(
        Type contractType,
        IClientInvoker invoker,
        ContractDescriptorFactory descriptorFactory,
        out object? proxy)
        => GeneratedContractRegistry.TryCreateProxy(
            contractType,
            invoker,
            descriptorFactory,
            out proxy);
}
