using Microsoft.Extensions.DependencyInjection;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal class MicrosoftDependencyResolver(IServiceProvider serviceProvider) : IDependencyResolver
{
    public TService? Resolve<TService>() where TService : class
    {
        return serviceProvider.GetService<TService>();
    }

    public object? Resolve(Type type)
    {
        return serviceProvider.GetService(type);
    }

    public IEnumerable<TService> ResolveAll<TService>() where TService : class
    {
        return serviceProvider.GetServices<TService>();
    }

    public IEnumerable<object?> ResolveAll(Type type)
    {
        return serviceProvider.GetServices(type);
    }
}