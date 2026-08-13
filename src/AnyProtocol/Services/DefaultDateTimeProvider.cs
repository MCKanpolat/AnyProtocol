using AnyProtocol.Abstraction;

namespace AnyProtocol.Services;

/// <summary>
/// Provides default date time values to AnyProtocol operations.
/// </summary>
public sealed class DefaultDateTimeProvider : IDateTimeProvider
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    /// <returns>The result of the get now operation.</returns>
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}
