using AnyProtocol.Validation.Abstraction;

namespace AnyProtocol.Validation;

/// <summary>
/// Contains the outcome of validation processing.
/// </summary>
public sealed class ValidationResult : IValidationResult
{
    private readonly Lazy<List<IValidationError>> _errors = new(() => new List<IValidationError>());

    /// <summary>
    /// Gets a value indicating whether validation succeeded.
    /// </summary>
    /// <value>true when is valid applies; otherwise, false.</value>
    public bool IsValid => !Errors.Any();

    /// <summary>
    /// Gets the validation errors collected for the request.
    /// </summary>
    public IEnumerable<IValidationError> Errors
    {
        get => _errors.Value;
    }

    /// <summary>
    /// Adds error support to the configuration.
    /// </summary>
    /// <param name="validationError">The validation error.</param>
    public void AddError(IValidationError validationError)
    {
        if (validationError is null)
        {
            throw new ArgumentNullException(nameof(validationError));
        }

        _errors.Value.Add(validationError);
    }
}