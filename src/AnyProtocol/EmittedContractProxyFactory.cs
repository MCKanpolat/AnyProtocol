using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AnyProtocol.Abstraction;
using AnyProtocol.Internals;

namespace AnyProtocol;

/// <summary>
/// Creates emitted contract proxy instances.
/// </summary>
public sealed class EmittedContractProxyFactory : IContractProxyFactory
{
    private readonly ContractDescriptorFactory _descriptorFactory;
    private readonly ContractProxyBuilder _proxyBuilder = new();

    /// <summary>
    /// Initializes a new instance of the EmittedContractProxyFactory class.
    /// </summary>
    /// <param name="descriptorFactory">The descriptor factory.</param>
    public EmittedContractProxyFactory(ContractDescriptorFactory descriptorFactory)
    {
        _descriptorFactory = descriptorFactory ?? throw new ArgumentNullException(nameof(descriptorFactory));
    }

    /// <summary>
    /// Creates &lt;t contract&gt;.
    /// </summary>
    /// <typeparam name="TContract">The contract type.</typeparam>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The result of the create&lt;t contract&gt; operation.</returns>
    [RequiresDynamicCode("Runtime contract proxy emission is unavailable with Native AOT. Use AnyProtocol.Generator.")]
    [RequiresUnreferencedCode("Runtime contract proxy emission requires contract method metadata.")]
    public TContract Create<TContract>(IClientInvoker invoker)
        where TContract : class
        => (TContract)Create(typeof(TContract), invoker);

    /// <summary>
    /// Creates .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="invoker">The invoker.</param>
    /// <returns>The result of the create operation.</returns>
    [RequiresDynamicCode("Runtime contract proxy emission is unavailable with Native AOT. Use AnyProtocol.Generator.")]
    [RequiresUnreferencedCode("Runtime contract proxy emission requires contract method metadata.")]
    public object Create(Type contractType, IClientInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        ArgumentNullException.ThrowIfNull(invoker);
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Cannot emit a AnyProtocol proxy for '{contractType.FullName}' because dynamic " +
                "code is unavailable. Reference AnyProtocol.Generator and register the closed " +
                "contract directly with LinkBuilder.AddClient<TContract>() or " +
                "AddServer<TContract, TImplementation>().");
        }

        var descriptor = _descriptorFactory.Create(contractType);
        var proxyType = _proxyBuilder.GetOrCreateProxyType(descriptor);
        return Activator.CreateInstance(proxyType, invoker, descriptor.Methods.ToArray())!;
    }
}
