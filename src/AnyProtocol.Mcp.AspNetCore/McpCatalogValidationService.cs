using Microsoft.Extensions.Hosting;

namespace AnyProtocol.Mcp.AspNetCore;

internal sealed class McpCatalogValidationService(McpToolCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.Tools;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
