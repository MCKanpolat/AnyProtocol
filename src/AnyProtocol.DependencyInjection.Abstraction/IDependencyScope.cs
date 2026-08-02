namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Defines operations for dependency scope.
/// </summary>
public interface IDependencyScope : IDisposable
{
    /// <summary>
    /// Gets the resolver.
    /// </summary>
    /// <value>The resolver.</value>
    IDependencyResolver Resolver { get; }
}