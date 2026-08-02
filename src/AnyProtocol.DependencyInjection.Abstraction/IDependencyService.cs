namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Defines operations for dependency service.
/// </summary>
public interface IDependencyService
{
    /// <summary>
    /// Adds  to the current configuration.
    /// </summary>
    /// <param name="serviceType">The runtime type to resolve.</param>
    /// <param name="implementationType">The implementation type.</param>
    /// <param name="lifetime">The lifetime.</param>
    /// <returns>The value produced by the operation.</returns>
    IDependencyService Add(Type serviceType, Type implementationType, ServiceLifetime lifetime);

    /// <summary>
    /// Provides the where implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <typeparam name="TImplementation">The implementation type.</typeparam>
    /// <param name="lifetime">The lifetime.</param>
    IDependencyService Add<TService, TImplementation>(ServiceLifetime lifetime) where TService : class where TImplementation : class, TService;

    /// <summary>
    /// Provides the add&lt;t service&gt; implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <param name="lifetime">The lifetime.</param>
    IDependencyService Add<TService>(ServiceLifetime lifetime) where TService : class;

    /// <summary>
    /// Provides the where implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TService">The service type.</typeparam>
    /// <typeparam name="TImplementation">The implementation type.</typeparam>
    IDependencyService AddTransient<TService, TImplementation>() where TService : class where TImplementation : class, TService;

    /// <summary>
    /// Provides the add&lt;t implementation&gt; implementation used by AnyProtocol applications.
    /// </summary>
    /// <typeparam name="TImplementation">The implementation type.</typeparam>
    /// <param name="service">The service.</param>
    IDependencyService Add<TImplementation>(TImplementation service) where TImplementation : class;

    /// <summary>
    /// Adds  to the current configuration.
    /// </summary>
    /// <typeparam name="TImplementation">The implementation type.</typeparam>
    /// <param name="serviceType">The runtime type to resolve.</param>
    /// <param name="factory">The factory used to create the instance.</param>
    /// <param name="lifetime">The lifetime.</param>
    /// <returns>The value produced by the operation.</returns>
    IDependencyService Add<TImplementation>(Type serviceType, Func<IDependencyResolver, TImplementation> factory, ServiceLifetime lifetime);
}