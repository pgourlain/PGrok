﻿using Spectre.Console;
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

        [CommandOption("-t| --tcpPort")]
        [Description("The port to listen. Switch to tcp mode when specified")]
        public int? TcpPort { get; set; }

        public override ValidationResult Validate()
        {
            if (Port is null)
            {
                var portEV = System.Environment.GetEnvironmentVariable("PGROK_PORT");
                if (int.TryParse(portEV, out int port))
                {
                    this.Port = port;
                }
            }
            if (useLocalhost is null)
            {
                var localhostEV = System.Environment.GetEnvironmentVariable("PGROK_LOCALHOST");
                if (bool.TryParse(localhostEV, out bool localhost))
                {
                    this.useLocalhost = localhost;
                }
            }
            if (useSingleTunnel is null)
            {
                var singleTunnelEV = System.Environment.GetEnvironmentVariable("PGROK_SINGLE_TUNNEL");
                if (bool.TryParse(singleTunnelEV, out bool singleTunnel))
                {
                    this.useSingleTunnel = singleTunnel;
                }
            }
            if (TcpPort is null)
            {
                var tcpPortEV = System.Environment.GetEnvironmentVariable("PGROK_TCPPORT");
                if (int.TryParse(tcpPortEV, out int tcpPort))
                {
                    this.TcpPort = tcpPort;
                }
            }

            if (TcpPort is not null && TcpPort < 1)
            {
                return ValidationResult.Error("tcpPort must be greater than 0.");
            }
            return base.Validate();
        }
    }
}
