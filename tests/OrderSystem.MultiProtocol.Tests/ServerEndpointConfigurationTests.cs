using Microsoft.Extensions.Configuration;
using OrderSystem.Server.Hosting;

namespace OrderSystem.MultiProtocol.Tests;

public sealed class ServerEndpointConfigurationTests
{
    [Fact]
    public void Configures_distinct_http1_and_http2_endpoints()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(OrderSystemServer).Assembly.Location)
            ?? throw new InvalidOperationException("The server assembly has no directory.");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(assemblyDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        Assert.Equal("http://localhost:5080", configuration["Kestrel:Endpoints:RestMcp:Url"]);
        Assert.Equal("Http1", configuration["Kestrel:Endpoints:RestMcp:Protocols"]);
        Assert.Equal("http://localhost:5081", configuration["Kestrel:Endpoints:Grpc:Url"]);
        Assert.Equal("Http2", configuration["Kestrel:Endpoints:Grpc:Protocols"]);
    }
}
