using AnyProtocol;
using AnyProtocol.DependencyInjection.Microsoft;
using AnyProtocol.Protocol.Abstraction;
using AnyProtocol.Serializer.TextJson;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderSystem.Client.Configuration;
using OrderSystem.Client.Services;
using OrderSystem.Contracts;
using GrpcClientProtocol = AnyProtocol.Protocol.Grpc.GrpcMessagingProtocol;
using RestClientProtocol = AnyProtocol.Protocol.Rest.RestMessagingProtocol;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    optional: false,
    reloadOnChange: false);
var settings = ClientSettings.From(builder.Configuration, args);
var serializer = new TextJsonMessageSerializer();
HttpClient? httpClient = null;
GrpcClientProtocol? grpcTransport = null;

builder.Services.AddAnyProtocol(link =>
{
    link.UseSerializer(serializer);

    if (settings.Protocol == ProtocolKey.Rest)
    {
        httpClient = new HttpClient { BaseAddress = settings.ServerUri };
        link.AddTransport(
            ProtocolKey.Rest,
            new RestClientProtocol(httpClient, serializer, "/api"));
    }
    else
    {
        var grpcChannel = GrpcChannel.ForAddress(settings.ServerUri);
        grpcTransport = new GrpcClientProtocol(grpcChannel, disposeChannel: true);
        link.AddTransport(
            ProtocolKey.Grpc,
            grpcTransport);
    }

    link.AddClient<IOrderService>(client => client
        .UseProtocol(settings.Protocol)
        .WithTimeout(TimeSpan.FromSeconds(10)));
});

using var host = builder.Build();
try
{
    await host.StartAsync();
    var service = host.Services.GetRequiredService<IOrderService>();
    await OrderScenario.RunAsync(
        service,
        settings.Protocol,
        settings.ShowError,
        Console.Out,
        CancellationToken.None);
}
catch (Exception exception) when (ConnectionFailure.IsConnectionLevel(exception))
{
    Console.Error.WriteLine($"Could not connect to {settings.ServerUri}.");
    Console.Error.WriteLine(
        "Start the server with: dotnet run --project samples/OrderSystem.MultiProtocol/OrderSystem.Server/OrderSystem.Server.csproj");
    return 1;
}
finally
{
    await host.StopAsync();
    if (grpcTransport is not null)
    {
        await grpcTransport.DisposeAsync();
    }

    httpClient?.Dispose();
}

return 0;
