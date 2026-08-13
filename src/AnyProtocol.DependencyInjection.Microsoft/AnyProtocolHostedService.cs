using AnyProtocol.Abstraction;
using Microsoft.Extensions.Hosting;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class AnyProtocolHostedService(
    IAnyProtocolBus bus,
    RuntimeLifetimeOwner runtimeLifetimeOwner) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => bus.StartAsync(cancellationToken).AsTask();

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await bus.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await runtimeLifetimeOwner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
