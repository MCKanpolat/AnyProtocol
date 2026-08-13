using AnyProtocol.Abstraction;
using AnyProtocol.Validation.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the validation pipeline stage.
/// </summary>
public sealed class ValidationFilter : IMessageFilter
{
    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        var services = MessageContextRuntime.Get(context).Services;
        if (context.Message is not null && services is not null && context.Method is not null)
        {
            var validatorType = typeof(IRequestValidator<>).MakeGenericType(context.Method.RequestType);
            var validator = services.Resolve(validatorType);
            if (validator is not null)
            {
                var validate = validatorType.GetMethod(nameof(IRequestValidator<object>.ValidateAsync))!;
                var pending = (ValueTask<IValidationResult>)validate.Invoke(
                    validator,
                    [context.Message, context.CancellationToken])!;
                var result = await pending.ConfigureAwait(false);
                if (!result.IsValid)
                {
                    throw new AnyProtocolValidationException(
                        new FaultMessage(
                            "validation_failed",
                            "Request validation failed.",
                            Details: result.Errors
                                .Select(error => new FaultDetail(error.PropertyName, error.Message))
                                .ToArray()));
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
