namespace AnyProtocol.Validation.Abstraction;

/// <summary>
/// Defines operations for validation.
/// </summary>
public interface IValidationResult
{
    /// <summary>
    /// Gets the is valid.
    /// </summary>
    /// <value>true when is valid applies; otherwise, false.</value>
    bool IsValid { get; }

    /// <summary>
    /// Gets the errors.
    /// </summary>
    /// <value>The errors.</value>
    IEnumerable<IValidationError> Errors { get; }

    /// <summary>
    /// Adds error to the current configuration.
    /// </summary>
    /// <param name="validationError">The validation error.</param>
    void AddError(IValidationError validationError);
}