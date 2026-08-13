using AnyProtocol.Authorization.Abstraction;
using AnyProtocol.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class AuthorizationConfigurationValidationService(
    RuntimePlan runtimePlan,
    IServiceProviderIsService serviceProviderIsService) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var protectedRoutes = runtimePlan.ServerRoutes
            .Where(static route => route.Method.RequiredPermissions.Count > 0)
            .ToArray();
        if (protectedRoutes.Length == 0)
        {
            return Task.CompletedTask;
        }

        var routeNames = string.Join(
            ", ",
            protectedRoutes.Select(
                static route => $"'{route.Method.ContractName}.{route.Method.MethodName}'"));
        if (!runtimePlan.RegisteredServerFilters.Any(static filter => filter is AuthorizationFilter))
        {
            throw new InvalidOperationException(
                $"Protected AnyProtocol routes {routeNames} require AuthorizationFilter. " +
                "Register it with LinkBuilder.AddServerFilter(new AuthorizationFilter()).");
        }

        if (!serviceProviderIsService.IsService(typeof(IAuthorizationProvider)))
        {
            throw new InvalidOperationException(
                $"Protected AnyProtocol routes {routeNames} require an IAuthorizationProvider " +
                "registration.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
