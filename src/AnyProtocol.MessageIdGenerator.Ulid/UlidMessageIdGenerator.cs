using AnyProtocol.Abstraction;

namespace AnyProtocol.MessageIdGenerator.Ulid;

/// <summary>
/// Provides the ulid message id generator implementation used by AnyProtocol applications.
/// </summary>
public sealed class UlidMessageIdGenerator : IMessageIdGenerator
{
    /// <summary>
    /// Generates .
    /// </summary>
    /// <returns>The result of the generate operation.</returns>
    public string Generate()
    {
        return System.Ulid.NewUlid()
            .ToString();
    }
}