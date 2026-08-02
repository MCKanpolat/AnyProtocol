using Microsoft.Extensions.DependencyInjection;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal class MicrosoftDependencyScope : IDependencyScope
{
    private readonly IServiceScope _scope;

    public MicrosoftDependencyScope(IServiceScope scope)
    {
        _scope = scope;
        Resolver = new MicrosoftDependencyResolver(scope.ServiceProvider);
    }

    public IDependencyResolver Resolver { get; }

    public void Dispose()
    {
        _scope.Dispose();
    }
}