namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for message id generator.
/// </summary>
public interface IMessageIdGenerator
{
    /// <summary>
    /// Generates .
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    string Generate();
}