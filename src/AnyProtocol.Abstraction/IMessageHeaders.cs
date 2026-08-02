namespace AnyProtocol.Abstraction;

/// <summary>
/// Defines operations for message headers.
/// </summary>
public interface IMessageHeaders : IReadOnlyDictionary<string, string>
{
    /// <summary>
    /// Indicates this[string].
    /// </summary>
    /// <value>The this[string].</value>
    new string? this[string key] { get; set; }

    /// <summary>
    /// Performs the set operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <param name="value">The value to store.</param>
    void Set(string key, string value);

    /// <summary>
    /// Performs the remove operation.
    /// </summary>
    /// <param name="key">The key that identifies the value.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    bool Remove(string key);
}