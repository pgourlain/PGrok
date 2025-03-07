using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using PGrokClient.Commands;
using Microsoft.Extensions.Options;
using PGrok.Common;


namespace PGrok.Client
{
    class LocalYARPServer
    {
        public static void Start(ClientSettings settings)
        {
            settings.ProxyPort ??= 5000;
            var args = new[] { "--urls", $"http://localhost:{settings.ProxyPort};https://localhost:{settings.ProxyPort + 1}" };

            var builder = WebApplication.CreateBuilder(args);

            // Add YARP services for forwarding to local web server
            builder.Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

            // Add background service for maintaining connection to public YARP
            builder.Services.AddHostedService<ReverseWebSocketTunnelService>();
            builder.Services.Configure<ClientSettings>((options) => {
                options.Debug = settings.Debug;
                options.LocalAddress = settings.LocalAddress;
                options.ProxyPort = settings.ProxyPort;
                options.ServerAddress = settings.ServerAddress;
                options.TunnelId = settings.TunnelId;
            });

            var app = builder.Build();

            // Enable WebSockets
            app.UseWebSockets();

            // Add YARP middleware for forwarding to local web server
            app.MapReverseProxy();

            app.Run();
        }
    }
}

// Background service that establishes and maintains the outbound connection to public YARP
public class ReverseWebSocketTunnelService : BackgroundService
{
    private readonly ILogger<ReverseWebSocketTunnelService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ClientSettings _options;

    public ReverseWebSocketTunnelService(
        ILogger<ReverseWebSocketTunnelService> logger,
        IConfiguration configuration, IOptions<ClientSettings> options)
    {
        _logger = logger;
        _configuration = configuration;
        _options  = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Establishing connection to public YARP server...");

                // Create the WebSocket connection to public YARP
                var publicYarpUrl = _options.ServerAddress?.TrimEnd('/');
                if (!string.IsNullOrEmpty(publicYarpUrl))
                {
                    publicYarpUrl = publicYarpUrl.Replace("http://", "ws://");
                    publicYarpUrl = publicYarpUrl.Replace("https://", "wss://");
                }
                var registrationToken = _configuration["TunnelConfig:RegistrationToken"];

                using var client = new ClientWebSocket();

                // Add authentication header for the connection
                client.Options.SetRequestHeader("X-Tunnel-Token", registrationToken);

                // Connect to the public YARP websocket endpoint
                await client.ConnectAsync(new Uri($"{publicYarpUrl}/tunnel/register"), stoppingToken);

                _logger.LogInformation("Connection established. Starting message forwarding.");

                // Process messages from public YARP
                await ProcessTunnelMessages(client, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in tunnel connection. Reconnecting in 5 seconds...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessTunnelMessages(ClientWebSocket webSocket, CancellationToken stoppingToken)
    {
        var buffer = new MemoryStream();

        while (webSocket.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
        {
            // Receive a message from the public YARP
            var receiveResult = await webSocket.ReceiveBytesAsync(buffer, stoppingToken);

            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing as requested by server",
                    stoppingToken);
                break;
            }

            await new LocalRequestHandler(_logger, _options.LocalAddress).HandleRequestAsync(buffer.GetBuffer(), webSocket, stoppingToken);
        }
    }
}
