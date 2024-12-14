

using PGrok.Server;

if (args.Length > 0 && args[0] == "--server")
{
    var argsDict = args.Skip(1).Select(arg => arg.Split('=')).ToDictionary(arg => arg[0], arg => arg[1]);
    argsDict.TryGetValue("--port", out var sport);
    if (!int.TryParse(sport, out var port))
    {
        port = 8080;
    }
    argsDict.TryGetValue("--localhost", out var sUseLocalhost);
    if (!bool.TryParse(sUseLocalhost, out var useLocalhost))
    {
        useLocalhost = false;
    }
    var server = new HttpTunnelServer(port, useLocalhost);

    await server.Start();
}
else
{
    Console.WriteLine("Usage: PGrok --server [--port=8080]");
}
