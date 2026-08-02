using AnyProtocol.Abstraction;
using AnyProtocol.Authorization.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the auth token pipeline stage.
/// </summary>
/// <param name="tokenProvider">The token provider.</param>
public sealed class AuthTokenFilter(IAuthTokenProvider tokenProvider) : IMessageFilter
{
    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        if (context.Direction == MessageDirection.Outbound)
        {
            var token = await tokenProvider.GetTokenAsync(context.CancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                context.Headers[HeaderNames.AuthToken] = token;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
