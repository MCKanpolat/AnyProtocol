namespace AnyProtocol.Validation.Abstraction;

/// <summary>Describes the result of request validation.</summary>
public interface IValidationResult
{
    /// <summary>Gets whether validation succeeded.</summary>
    bool IsValid { get; }

    /// <summary>Gets the validation errors.</summary>
    IEnumerable<IValidationError> Errors { get; }

    /// <summary>Adds one validation error.</summary>
    void AddError(IValidationError validationError);
}
