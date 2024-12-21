using PGrok.Commands;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PGrokClient.Commands
{
    public class ClientSettings : LogCommandSettings
    {
        [CommandOption("-t| --tunnelId")]
        [Description("The tunnelId to use. will represents serviceName on server side.")]
        public string? TunnelId { get; set; }

        [CommandOption("-s| --serverAddress")]
        [Description("The server address. Specify http(s)://my_server_url, it will switch automatically to ws(s) protocol")]
        public string? ServerAddress { get; set; }

        [CommandOption("-l| --localAddress")]
        [Description("The local address to use for calls redirections. Specify http(s)://my_local_url")]
        public string? LocalAddress { get; set; }

        [CommandOption("-r| --proxyPort")]
        [Description("Listen on this port to proxy calls to serverAddress.")]
        public int? ProxyPort { get; set; }

    }
}
