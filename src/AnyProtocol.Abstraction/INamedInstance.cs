namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for named instance.
/// </summary>
/// <typeparam name="T">The type of the named instance.</typeparam>
public interface INamedInstance<out T> where T : IComparable, IConvertible, IEquatable<T>
{
    /// <summary>
    /// Gets the tag.
    /// </summary>
    /// <value>The tag.</value>
    T Tag { get; }
}