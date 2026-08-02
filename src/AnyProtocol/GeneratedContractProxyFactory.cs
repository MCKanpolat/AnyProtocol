using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Creates generated contract proxy instances.
/// </summary>
public sealed class GeneratedContractProxyFactory : IContractProxyFactory
{
    private readonly ContractDescriptorFactory _descriptorFactory;
    private EmittedContractProxyFactory? _emittedFallback;

    /// <summary>
    /// Initializes a new instance of the GeneratedContractProxyFactory class.
    /// </summary>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    public GeneratedContractProxyFactory(ContractDescriptorFactory descriptorFactory)
    {
        _descriptorFactory = descriptorFactory ??
                             throw new ArgumentNullException(nameof(descriptorFactory));
    }

    /// <summary>
    /// Creates &lt;t contract&gt;.
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The result of the create&lt;t contract&gt; operation.</returns>
    public TContract Create<TContract>(IClientInvoker invoker)
        where TContract : class
        => (TContract)Create(typeof(TContract), invoker);

    /// <summary>
    /// Creates .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The result of the create operation.</returns>
    public object Create(Type contractType, IClientInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(invoker);
        if (GeneratedContractProxyRegistry.TryCreate(
                contractType,
                invoker,
                _descriptorFactory,
                out var proxy))
        {
            return proxy!;
        }

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"No generated AnyProtocol proxy is registered for '{contractType.FullName}'. " +
                "Dynamic proxy emission is unavailable. Reference AnyProtocol.Generator and " +
                "register the closed contract directly with LinkBuilder.AddClient<TContract>() " +
                "or AddServer<TContract, TImplementation>() when publishing with Native AOT.");
        }

        return (_emittedFallback ??= new EmittedContractProxyFactory(_descriptorFactory))
            .Create(contractType, invoker);
    }
}
