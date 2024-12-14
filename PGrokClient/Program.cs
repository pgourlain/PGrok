using Microsoft.Extensions.DependencyInjection;
using PGrok.Client;
using PGrokClient.Commands;
using PGrokClient.Services;
using Spectre.Console.Cli;

IServiceCollection services = new ServiceCollection();
services.AddLogging();
var registrar = new TypeRegistrar(services);


// Setup the application itself
var app = new CommandApp(registrar);

app.Configure(config => {
    
    config.SetApplicationName("pgrok-client");
    config.AddCommand<ClientStartCommand>("start")
        .WithDescription("Start the client")
        .WithExample(new[] { "start --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=http://localhost:5000" });
});
app.SetDefaultCommand<ClientStartCommand>();
// Run the application
return await app.RunAsync(args);


//if (args.Length > 0 && args.Length == 3)
//{
//    //parse args in dictionary
//    var argsDict = args.Select(arg => arg.Split('=')).ToDictionary(arg => arg[0], arg => arg[1]);
//    CheckOptions(argsDict, "--tunnelId", "Missing tunnelId");
//    CheckOptions(argsDict, "--serverAddress", "Missing serverAddress");
//    CheckOptions(argsDict, "--localAddress", "Missing localAddress");
//    var client = new HttpTunnelClient(argsDict["--serverAddress"], argsDict["--tunnelId"], argsDict["--localAddress"]);
//    await client.Start();
//}
//else
//{
//    Console.WriteLine("Usage: PGrok --tunnelId=1234 --serverAddress=https://pgrok.azurecontainerapps.io --localAddress=http://localhost:5000");
//}
//void CheckOptions(Dictionary<string, string> argsDict, string key, string message)
//{
//    if (!argsDict.TryGetValue(key, out _))
//    {
//        throw new ArgumentException(message);
//    }
//}