using Microsoft.Extensions.Logging;
using PGrok.Client;
using Spectre.Console;
using Spectre.Console.Cli;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PGrokClient.Commands
{
    internal class ClientStartCommand : AsyncCommand<ClientSettings>
    {
        private readonly ILogger<ClientStartCommand> logger;

        public ClientStartCommand(ILogger<ClientStartCommand> logger)
        {
            this.logger = logger;
        }
        public override ValidationResult Validate(CommandContext context, ClientSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.TunnelId))
            {
                return ValidationResult.Error("tunnelId must be specified. It represents the service that you want to redirect to local.");
            }   
            if (string.IsNullOrWhiteSpace(settings.ServerAddress))
            {
                return ValidationResult.Error("serverAddress must be specified.");
            }   
            if (string.IsNullOrWhiteSpace(settings.LocalAddress))
            {
                return ValidationResult.Error("localAddress must be specified. it's local url use to redirect call from remote server (specified by serverAddress).");
            }   

            return base.Validate(context, settings);
        }

        public override async Task<int> ExecuteAsync(CommandContext context, ClientSettings settings)
        {
            var client = new HttpTunnelClient(settings.ServerAddress!, settings.TunnelId!, settings.LocalAddress!, logger);
            await client.Start();
            return 0;
        }
    }
}
