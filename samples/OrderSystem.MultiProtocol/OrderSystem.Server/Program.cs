using OrderSystem.Server.Hosting;

var builder = WebApplication.CreateBuilder(args);
OrderSystemServer.ConfigureServices(builder.Services);

var app = builder.Build();
OrderSystemServer.MapEndpoints(app);
await app.RunAsync();
