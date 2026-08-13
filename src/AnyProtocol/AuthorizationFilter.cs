using AnyProtocol.Abstraction;
using AnyProtocol.Authorization.Abstraction;

namespace AnyProtocol;

/// <summary>
/// Processes messages in the authorization pipeline stage.
/// </summary>
public sealed class AuthorizationFilter : IMessageFilter
{
    /// <summary>
    /// Invokes the configured operation asynchronously.
    /// </summary>
    /// <param name="context">The context for the current operation.</param>
    /// <param name="next">The next.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask InvokeAsync(IMessageContext context, MessageFilterDelegate next)
    {
        var permissions = context.Method?.RequiredPermissions;
        if (permissions is { Count: > 0 })
        {
            var provider = MessageContextRuntime.Get(context).Services?.Resolve<IAuthorizationProvider>() ??
                           throw new AnyProtocolFaultException(
                               new FaultMessage(
                                   "authorization_unavailable",
                                   "No authorization provider is registered."));
            if (!await provider.CheckPermissionsAsync(permissions, context.CancellationToken)
                    .ConfigureAwait(false))
            {
                throw new AnyProtocolFaultException(
                    new FaultMessage("permission_denied", "Required permissions were not granted."));
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
