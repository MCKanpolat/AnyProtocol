using AnyProtocol.Abstraction;

namespace AnyProtocol.Services;

/// <summary>
/// Provides the default message id generator implementation used by AnyProtocol applications.
/// </summary>
public sealed class DefaultMessageIdGenerator : IMessageIdGenerator
{
    /// <summary>
    /// Generates .
    /// </summary>
    /// <returns>The result of the generate operation.</returns>
    public string Generate()
    {
        return Guid.NewGuid()
            .ToString("N");
    }
}
