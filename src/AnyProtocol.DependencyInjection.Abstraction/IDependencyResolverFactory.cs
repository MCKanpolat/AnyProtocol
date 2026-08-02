namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Defines operations for dependency resolver.
/// </summary>
public interface IDependencyResolverFactory
{
    /// <summary>
    /// Creates resolver.
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    IDependencyResolver CreateResolver();

    /// <summary>
    /// Creates async scope.
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    IAsyncDependencyScope CreateAsyncScope();
}