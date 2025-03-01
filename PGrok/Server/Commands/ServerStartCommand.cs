using Microsoft.Extensions.Logging;
using PGrok.Monitoring;
using PGrok.Security;
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
                var cfg = new PGrok.Server.HttpTunnelServer.ServerConfiguration {
                    Port = settings.Port ?? 8080,
                    UseLocalhost = settings.useLocalhost ?? false,
                    UseSingleTunnel = settings.useSingleTunnel ?? false,
                    ProxyPort = settings.ProxyPort ?? 8080
                };
                var server = new HttpTunnelServer(logger, _httpClientFactory, cfg);

                // Add security if needed
                var authConfig = new TunnelAuthConfig {
                    EnableAuthentication = true,
                    EnableRateLimiting = true,
                    DefaultRateLimit = 100,
                    RateLimitWindowSeconds = 60
                };
                var authService = new TunnelAuthenticationService(logger, authConfig);

                // Add monitoring
                var monitoringConfig = new TunnelMonitoringConfig {
                    EnablePeriodicLogging = true,
                    LoggingIntervalSeconds = 60,
                    SlowRequestThresholdSeconds = 5
                };
                var monitoringService = new TunnelMonitoringService(logger, monitoringConfig);

                await server.StartAsync();
                return 0;
            }
        }
    }
}
