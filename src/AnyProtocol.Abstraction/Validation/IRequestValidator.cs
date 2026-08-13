namespace AnyProtocol.Validation.Abstraction;

/// <summary>Validates one request type.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestValidator<in TRequest> where TRequest : class
{
    /// <summary>Validates a request asynchronously.</summary>
    ValueTask<IValidationResult> ValidateAsync(
        TRequest request,
        CancellationToken cancellationToken = default);
}
