using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Commands
{
    public class ServerSettings : LogCommandSettings
    {
        [CommandOption("-p| --port")]
        [Description("The port to listen. 8080 is use if not provided.")]
        public int? Port { get; set; }


        [CommandOption("-l| --localhost")]
        [Description("Use localhost instead of")]
        public bool? useLocalhost { get; set; }

        [CommandOption("-s| --singleTunnel")]
        [Description("Use single tunnel. Useful for service replacement")]
        public bool? useSingleTunnel { get; set; }
    }
}
