using Microsoft.Extensions.DependencyInjection;
using AnyProtocol.DependencyInjection.Abstraction;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal class AsyncMicrosoftDependencyScope : IAsyncDependencyScope
{
    private readonly AsyncServiceScope _scope;

    public AsyncMicrosoftDependencyScope(AsyncServiceScope scope)
    {
        _scope = scope;
        Resolver = new MicrosoftDependencyResolver(scope.ServiceProvider);
    }

    public IDependencyResolver Resolver { get; }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
    }
}