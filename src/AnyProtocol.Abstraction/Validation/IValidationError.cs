namespace AnyProtocol.Validation.Abstraction;

/// <summary>Describes one validation error.</summary>
public interface IValidationError
{
    /// <summary>Gets or sets the validation message.</summary>
    string Message { get; set; }

    /// <summary>Gets or sets the invalid property name.</summary>
    string PropertyName { get; set; }
}
