using AnyProtocol.Abstraction;

namespace AnyProtocol.SchemaRegistry.Abstraction;

/// <summary>
/// Defines operations for message type resolver.
/// </summary>
public interface IMessageTypeResolver
{
    /// <summary>
    /// Resolves async.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<Type> ResolveAsync(IMessageContext context);
}