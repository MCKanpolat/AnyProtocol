namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for contract proxy.
/// </summary>
public interface IContractProxyFactory
{
    /// <summary>
    /// Creates .
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The value produced by the operation.</returns>
    TContract Create<TContract>(IClientInvoker invoker)
        where TContract : class;

    /// <summary>
    /// Creates .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The value produced by the operation.</returns>
    object Create(Type contractType, IClientInvoker invoker);
}
