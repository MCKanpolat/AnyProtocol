using AnyProtocol.Abstraction;

namespace AnyProtocol.Services;

/// <summary>
/// Provides default date time values to AnyProtocol operations.
/// </summary>
public sealed class DefaultDateTimeProvider : IDateTimeProvider
{
    /// <summary>
    /// Gets now.
    /// </summary>
    /// <returns>The result of the get now operation.</returns>
    public DateTime GetNow() => DateTime.UtcNow;
}