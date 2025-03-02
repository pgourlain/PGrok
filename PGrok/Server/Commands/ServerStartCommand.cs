using Microsoft.Extensions.Logging;
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
            PublicYARPServer.Start(settings);
            return 0;
        }
    }
}
