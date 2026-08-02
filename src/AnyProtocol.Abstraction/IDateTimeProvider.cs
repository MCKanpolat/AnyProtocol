namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for date time.
/// </summary>
public interface IDateTimeProvider
{
    /// <summary>
    /// Gets now.
    /// </summary>
    /// <returns>The value produced by the operation.</returns>
    DateTime GetNow();
}