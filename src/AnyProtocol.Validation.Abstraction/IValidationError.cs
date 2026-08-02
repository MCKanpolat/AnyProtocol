namespace AnyProtocol.Validation.Abstraction;

/// <summary>
/// Defines operations for validation error.
/// </summary>
public interface IValidationError
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    /// <value>The message.</value>
    string Message { get; set; }

    /// <summary>
    /// Gets the property name.
    /// </summary>
    /// <value>The property name.</value>
    string PropertyName { get; set; }
}