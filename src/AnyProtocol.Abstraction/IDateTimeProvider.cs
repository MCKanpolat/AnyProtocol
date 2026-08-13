namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for date time.
/// </summary>
public interface IDateTimeProvider
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    DateTimeOffset GetUtcNow();
}
