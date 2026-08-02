namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Defines operations for async dependency scope.
/// </summary>
public interface IAsyncDependencyScope : IAsyncDisposable
{
    /// <summary>
    /// Gets the resolver.
    /// </summary>
    /// <value>The resolver.</value>
    IDependencyResolver Resolver { get; }
}