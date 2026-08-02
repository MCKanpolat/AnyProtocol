using AnyProtocol.DependencyInjection.Abstraction;
using Microsoft.Extensions.DependencyInjection;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class MicrosoftDependencyResolverFactory(IServiceProvider serviceProvider)
    : IDependencyResolverFactory
{
    public IDependencyResolver CreateResolver()
        => new MicrosoftDependencyResolver(serviceProvider);

    public IAsyncDependencyScope CreateAsyncScope()
        => new AsyncMicrosoftDependencyScope(serviceProvider.CreateAsyncScope());
}
