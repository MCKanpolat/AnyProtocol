namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Defines operations for dependency resolver.
/// </summary>
public interface IDependencyResolver
{
    /// <summary>
    /// Provides the resolve&lt;t service&gt; implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    TService? Resolve<TService>() where TService : class;

    /// <summary>
    /// Resolves .
    /// </summary>
    /// <param name="serviceType">The runtime type to resolve.</param>
    /// <returns>The value produced by the operation.</returns>
    object? Resolve(Type serviceType);

    /// <summary>
    /// Provides the resolve all&lt;t service&gt; implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    IEnumerable<TService> ResolveAll<TService>() where TService : class;

    /// <summary>
    /// Resolves all registered  instances.
    /// </summary>
    /// <param name="serviceType">The runtime type to resolve.</param>
    /// <returns>The value produced by the operation.</returns>
    IEnumerable<object?> ResolveAll(Type serviceType);
}