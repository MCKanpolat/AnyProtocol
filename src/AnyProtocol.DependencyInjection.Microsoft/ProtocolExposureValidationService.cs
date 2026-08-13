using AnyProtocol.Configuration;
using AnyProtocol.Protocol.Abstraction;
using Microsoft.Extensions.Hosting;

namespace AnyProtocol.DependencyInjection.Microsoft;

internal sealed class ProtocolExposureValidationService(
    RuntimePlan runtimePlan,
    ProtocolExposureRegistry exposureRegistry,
    IEnumerable<IProtocolExposureDeclaration> declarations) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var requiresMcp = runtimePlan.ServerRegistrations.Any(
            registration => registration.Protocols.Contains(ProtocolKey.Mcp));
        if (!requiresMcp)
        {
            return Task.CompletedTask;
        }

        var mcpDeclarations = declarations
            .Where(declaration => declaration.Protocol == ProtocolKey.Mcp)
            .ToArray();
        if (mcpDeclarations.Length == 0)
        {
            throw new InvalidOperationException(
                "MCP is selected by a server registration, but no MCP host adapter is registered. " +
                "Call AddAnyProtocolMcp() for HTTP or AddAnyProtocolMcpStdio() for stdio.");
        }

        if (mcpDeclarations.Any(declaration => !declaration.RequiresEndpointMapping) ||
            exposureRegistry.IsMapped(ProtocolKey.Mcp))
        {
            return Task.CompletedTask;
        }

        throw new InvalidOperationException(
            "MCP HTTP hosting is registered, but its endpoint is not mapped. " +
            "Call MapAnyProtocolMcp() before starting the application.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
