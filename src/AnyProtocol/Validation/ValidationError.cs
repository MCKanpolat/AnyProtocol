using AnyProtocol.Validation.Abstraction;

namespace AnyProtocol.Validation;

/// <summary>
/// Provides the validation error implementation used by AnyProtocol applications.
/// </summary>
/// <param name="message">The message payload or description.</param>
/// <param name="propertyName">The property name.</param>
public sealed class ValidationError(string? message, string? propertyName) : IValidationError
{
    /// <summary>
    /// Gets or initializes the message.
    /// </summary>
    /// <value>The message.</value>
    public string Message { get;set; } = message ?? throw new ArgumentNullException(nameof(message));

    /// <summary>
    /// Gets or initializes the property name.
    /// </summary>
    /// <value>The property name.</value>
    public string PropertyName { get;set; } = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
}