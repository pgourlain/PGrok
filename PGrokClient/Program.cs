using Microsoft.Extensions.DependencyInjection;
using PGrok.Client;
using PGrokClient.Commands;
using PGrok.Services;
using Spectre.Console.Cli;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.Reflection;

IServiceCollection services = new ServiceCollection();
services.AddLogging(builder => {
    builder.AddConsole(options =>
    {
        options.FormatterName = "pgrok";
    })
    .AddConsoleFormatter<CustomConsoleFormatter, ConsoleFormatterOptions>()
    .SetMinimumLevel(LogLevel.Information);
});
var registrar = new TypeRegistrar(services);

// Setup the application itself
var app = new CommandApp(registrar);

app.Configure(config => {

    config.SetApplicationName("pgrok");
    config.UseAssemblyInformationalVersion();
    config.AddCommand<ClientStartCommand>("start")
        .WithDescription("Start the client")
        .WithExample(new[] { "start --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=http://localhost:5000" });
    config.AddCommand<ClientTcpStartCommand>("start-tcp")
        .WithDescription("Start the client on tcp mode")
        .WithExample(new[] { "start-tcp --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=localhost:3306" });
});
app.SetDefaultCommand<ClientStartCommand>();
// Run the application
return await app.RunAsync(args);
