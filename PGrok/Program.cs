using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging;
using PGrok.Services;
using Spectre.Console.Cli;
using PGrok.Server.Commands;
using PGrokClient.Commands;
using PGrok.Commands;

IServiceCollection services = new ServiceCollection();
services.AddLogging(builder => {
    var loggingBuilder = builder.AddConsole(options => {
        options.FormatterName = "pgrok";
    })
    .AddConsoleFormatter<CustomConsoleFormatter, ConsoleFormatterOptions>();

    if (Environment.GetCommandLineArgs().Any(x => x == "--debug"))
    {
        loggingBuilder.SetMinimumLevel(LogLevel.Debug);
    }
    else
    {
        loggingBuilder.SetMinimumLevel(LogLevel.Information);
    }
});
services.AddHttpClient();
var registrar = new TypeRegistrar(services);


// Setup the application itself
var app = new CommandApp(registrar);

app.Configure(config => {
    config.SetApplicationName("pgrok");
    config.UseAssemblyInformationalVersion();
    config.AddCommand<ServerStartCommand>("start-server")
        .WithDescription("Start the server in http mode")
        .WithExample(new[] { "start-server --port=8080 --localhost" })
        .WithExample(new[] { "start-server --port=8080 --tcpPort=3306 --localhost" });
    config.AddCommand<ClientStartCommand>("start")
        .WithDescription("Start the client")
        .WithExample(new[] { "start --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=http://localhost:5000" });
    //config.AddCommand<ClientTcpStartCommand>("start-tcp")
    //    .WithDescription("Start the client on tcp mode")
    //    .WithExample(new[] { "start-tcp --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=localhost:3306" });
});
app.SetDefaultCommand<ClientStartCommand>();
// Run the application
return await app.RunAsync(args);


