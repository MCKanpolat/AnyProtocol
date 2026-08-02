namespace AnyProtocol.DependencyInjection.Abstraction;

/// <summary>
/// Identifies the available service lifetime values.
/// </summary>
public enum ServiceLifetime
{
    /// <summary>
    /// Indicates singleton.
    /// </summary>
    Singleton,
    /// <summary>
    /// Indicates scoped.
    /// </summary>
    Scoped,
    /// <summary>
    /// Indicates transient.
    /// </summary>
    Transient
}