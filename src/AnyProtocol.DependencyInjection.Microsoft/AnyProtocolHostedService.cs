using AnyProtocol.Abstraction;
using Microsoft.Extensions.Hosting;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class AnyProtocolHostedService(IAnyProtocolBus bus) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
        => bus.StartAsync(cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken)
        => bus.StopAsync(cancellationToken).AsTask();
}
