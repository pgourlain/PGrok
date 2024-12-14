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
            return base.Validate(context, settings);
        }
        public override async Task<int> ExecuteAsync(CommandContext context, ClientSettings settings)
        {
            var client = new HttpTunnelClient(settings.ServerAddress!, settings.TunnelId!, settings.LocalAddress!);
            await client.Start();
            return 0;
        }
    }
}
