using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PGrok.Server.Commands;
using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PGrok.Server
{
    class PublicYARPServer
    {
        public static void Start(ServerSettings settings)
        {
            settings.Port ??= 8080;
            var localhost = (settings.useLocalhost ?? false) ? "localhost" : "+";
            var args = new[] { "--urls", $"http://{localhost}:{settings.Port};https://{localhost}:{settings.Port + 1}" };
 
            var builder = WebApplication.CreateBuilder(args);

            // Add services for WebSocket connections and tunnel management
            builder.Services.AddSingleton<TunnelConnectionManager>();

            // Add YARP services
            builder.Services.AddReverseProxy()
                .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

            var app = builder.Build();

            // Enable WebSockets
            app.UseWebSockets();

            // Setup the tunnel registration endpoint
            app.Map("/tunnel/register", async context => {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                // Validate token (simple example - use better auth in production)
                string token = context.Request.Headers["X-Tunnel-Token"];
                if (string.IsNullOrEmpty(token) || token != app.Configuration["TunnelConfig:AuthToken"])
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                // Accept the WebSocket connection
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                var tunnelManager = context.RequestServices.GetRequiredService<TunnelConnectionManager>();

                // Generate a tunnel ID for this connection
                string tunnelId = Guid.NewGuid().ToString();
                logger.LogInformation("Tunnel {TunnelId} established", tunnelId);

                // Add the connection to our manager
                await tunnelManager.AddConnectionAsync(tunnelId, webSocket);

                // Keep the connection alive until it's closed
                try
                {
                    var buffer = new byte[1024];
                    while (webSocket.State == WebSocketState.Open)
                    {
                        var result = await webSocket.ReceiveAsync(
                            new ArraySegment<byte>(buffer), CancellationToken.None);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await webSocket.CloseAsync(
                                WebSocketCloseStatus.NormalClosure,
                                "Closing",
                                CancellationToken.None);
                            break;
                        }

                        // Process tunnel messages - in a real implementation, these would be
                        // responses from the local server to forward back to clients
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in tunnel {TunnelId}", tunnelId);
                }
                finally
                {
                    await tunnelManager.RemoveConnectionAsync(tunnelId);
                    logger.LogInformation("Tunnel {TunnelId} closed", tunnelId);
                }
            });

            // Add middleware to intercept client requests and tunnel them
            app.Use(async (context, next) => {
                // Skip tunnel registration endpoint
                if (context.Request.Path.StartsWithSegments("/tunnel"))
                {
                    await next();
                    return;
                }

                var tunnelManager = context.RequestServices.GetRequiredService<TunnelConnectionManager>();
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

                // Get an available tunnel
                var tunnel = tunnelManager.GetAvailableTunnel();
                if (tunnel == null)
                {
                    logger.LogWarning("No available tunnels for request to {Path}", context.Request.Path);
                    context.Response.StatusCode = 503; // Service Unavailable
                    await context.Response.WriteAsync("No tunnel connections available. Please try again later.");
                    return;
                }

                // Serialize the HTTP request to send through the tunnel
                var requestData = await SerializeHttpRequestAsync(context.Request);

                // Send the request through the tunnel
                await tunnel.SendAsync(
                    new ArraySegment<byte>(requestData),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);

                // Wait for and process the response
                var responseBuffer = new byte[8192];
                var responseResult = await tunnel.ReceiveAsync(
                    new ArraySegment<byte>(responseBuffer),
                    CancellationToken.None);

                // Deserialize and apply the HTTP response
                await DeserializeAndApplyHttpResponseAsync(
                    responseBuffer.AsMemory(0, responseResult.Count),
                    context.Response);
            });

            // Add standard YARP middleware (as fallback/alternative)
            app.MapReverseProxy();

            app.Run();

        }

        // Helper methods for serializing HTTP requests/responses through the WebSocket tunnel
        private static async Task<byte[]> SerializeHttpRequestAsync(HttpRequest request)
        {
            // In a real implementation, you would serialize the request including:
            // - Method, path, query
            // - Headers
            // - Body

            // This is a simplified example
            var requestBody = new byte[0];
            if (request.Body.CanRead)
            {
                using var ms = new System.IO.MemoryStream();
                await request.Body.CopyToAsync(ms);
                requestBody = ms.ToArray();
            }

            // Create a simple JSON-like format for the request
            var serialized = Encoding.UTF8.GetBytes($"{{\"method\":\"{request.Method}\",\"path\":\"{request.Path}\",\"body\":\"{Convert.ToBase64String(requestBody)}\"}}");

            return serialized;
        }

        private static async Task DeserializeAndApplyHttpResponseAsync(Memory<byte> data, HttpResponse response)
        {
            // In a real implementation, you would deserialize the response including:
            // - Status code
            // - Headers
            // - Body

            // This is a simplified example
            var responseText = Encoding.UTF8.GetString(data.Span);

            // Parse the response (simple example)
            response.StatusCode = 200; // Default

            // Write the body
            await response.WriteAsync(responseText);
        }
    }
}




// Helper class to manage WebSocket tunnel connections
public class TunnelConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _connections = new();
    private readonly ILogger<TunnelConnectionManager> _logger;

    public TunnelConnectionManager(ILogger<TunnelConnectionManager> logger)
    {
        _logger = logger;
    }

    public Task AddConnectionAsync(string id, WebSocket webSocket)
    {
        _connections[id] = webSocket;
        _logger.LogInformation("Added tunnel connection {Id}. Total connections: {Count}",
            id, _connections.Count);
        return Task.CompletedTask;
    }

    public Task RemoveConnectionAsync(string id)
    {
        _connections.TryRemove(id, out _);
        _logger.LogInformation("Removed tunnel connection {Id}. Total connections: {Count}",
            id, _connections.Count);
        return Task.CompletedTask;
    }

    public WebSocket GetAvailableTunnel()
    {
        // Simple round-robin selection (could be improved with load balancing)
        foreach (var connection in _connections)
        {
            if (connection.Value.State == WebSocketState.Open)
                return connection.Value;
        }

        return null;
    }
}


