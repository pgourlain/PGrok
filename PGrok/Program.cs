using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging;
using PGrok.Commands;
using PGrok.Services;
using Spectre.Console.Cli;

IServiceCollection services = new ServiceCollection();
services.AddLogging(builder => {
    builder.AddConsole(options => {
        options.FormatterName = "pgrok";
    })
    .AddConsoleFormatter<CustomConsoleFormatter, ConsoleFormatterOptions>()
    .SetMinimumLevel(LogLevel.Information);
});
var registrar = new TypeRegistrar(services);


// Setup the application itself
var app = new CommandApp(registrar);

app.Configure(config => {

    config.SetApplicationName("pgrok-server");
    config.AddCommand<ServerStartCommand>("start")
        .WithDescription("Start the server in http mode")
        .WithExample(new[] { "start --port=8080 --localhost" })
        .WithExample(new[] { "start --port=8080 --tcpPort=3306 --localhost" });
});
app.SetDefaultCommand<ServerStartCommand>();
// Run the application
return await app.RunAsync(args);
