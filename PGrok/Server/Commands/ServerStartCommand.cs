﻿using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Server.Commands
{
    internal class ServerStartCommand : AsyncCommand<ServerSettings>
    {
        private readonly ILogger<ServerStartCommand> logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public ServerStartCommand(ILogger<ServerStartCommand> logger, IHttpClientFactory httpClientFactory)
        {
            this.logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public override ValidationResult Validate(CommandContext context, ServerSettings settings)
        {
            return base.Validate(context, settings);
        }


        public override async Task<int> ExecuteAsync(CommandContext context, ServerSettings settings)
        {
            if (settings.TcpPort is not null)
            {
                int tcp = (int)settings.TcpPort;
                var server = new TcpTunnelServer(logger, settings.Port ?? 8080, tcp, settings.useLocalhost ?? false);
                await server.Start();
                return 0;
            }
            else
            {
                var server = new HttpTunnelServer(logger, _httpClientFactory, settings.Port ?? 8080, settings.useLocalhost ?? false,
                        settings.useSingleTunnel ?? false, settings.ProxyPort ?? 8080);
                await server.Start();
                return 0;
            }
        }
    }
}
