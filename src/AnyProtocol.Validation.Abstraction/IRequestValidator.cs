namespace AnyProtocol.Validation.Abstraction;

/// <summary>
/// Defines operations for request validator.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestValidator<in TRequest> where TRequest : class
{
    /// <summary>
    /// Sends a request and waits asynchronously for its response.
    /// </summary>
    /// <param name="request">The request to process.</param>
    /// <returns>A task whose result contains the value produced by the operation.</returns>
    ValueTask<IValidationResult> RequestAsync(TRequest request);
}