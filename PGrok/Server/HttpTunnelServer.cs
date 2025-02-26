using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Server;

public class HttpTunnelServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, IPGROKTunnel> _tunnels;
    private readonly ILogger _logger;
    private readonly ServerConfiguration _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _listenerTask;
    private bool _disposed;

    // Configuration object to replace multiple parameters
    public class ServerConfiguration
    {
        public int Port { get; set; } = 80;
        public bool UseLocalhost { get; set; } = false;
        public bool UseSingleTunnel { get; set; } = false;
        public int ProxyPort { get; set; } = 8080;
        public TimeSpan TunnelTimeout { get; set; } = TimeSpan.FromMinutes(30);
        public int MaxConcurrentRequests { get; set; } = 100;
    }

    public HttpTunnelServer(ILogger logger, IHttpClientFactory httpClientFactory, ServerConfiguration? config = null)
    {
        _config = config ?? new ServerConfiguration();
        _logger = logger;
        _listener = new HttpListener();
        _tunnels = new ConcurrentDictionary<string, IPGROKTunnel>();
        _httpClientFactory = httpClientFactory;
        _disposed = false;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listenerTask != null)
        {
            throw new InvalidOperationException("Server is already running");
        }

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var host = _config.UseLocalhost ? "localhost" : "+";
        _listener.Prefixes.Clear();
        _listener.Prefixes.Add($"http://{host}:{_config.Port}/");
        _listener.Start();

        _logger.LogInformation($"Server listening on port {_config.Port}");
        if (_config.UseSingleTunnel)
        {
            _logger.LogInformation("Single tunnel mode enabled");
        }
        _logger.LogInformation($"Server dispatch http calls from clients on port {_config.ProxyPort}");
        _logger.LogInformation("Ready to accept connections");

        // Start background cleanup task for idle tunnels
        _ = RunPeriodicTunnelCleanupAsync(_cancellationTokenSource.Token);

        // Start the listener as a background task
        _listenerTask = Task.Run(async () => {
            try
            {
                await RunListenerLoopAsync(_cancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected when cancellation is requested
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in listener loop");
            }
        }, cancellationToken);
        return _listenerTask;
    }

    private async Task RunListenerLoopAsync(CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(_config.MaxConcurrentRequests);

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
                continue;
            }

            // Use a semaphore to limit concurrent requests
            await semaphore.WaitAsync(cancellationToken);

            _ = Task.Run(async () => {
                try
                {
                    await HandleContextAsync(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing request");
                    try
                    {
                        context.Response.StatusCode = 500;
                        context.Response.Close();
                    }
                    catch
                    {
                        // Ignore errors when trying to send error response
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken);
        }
    }

    private async Task RunPeriodicTunnelCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                CleanupIdleTunnels();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in tunnel cleanup task");
        }
    }

    private void CleanupIdleTunnels()
    {
        var idleThreshold = DateTime.UtcNow - _config.TunnelTimeout;
        var idleTunnels = _tunnels.Where(kvp =>
            kvp.Value is WebSocketTunnel wst &&
            wst.LastActivity < idleThreshold);

        foreach (var tunnel in idleTunnels)
        {
            _logger.LogInformation($"Removing idle tunnel: {tunnel.Key}");
            if (_tunnels.TryRemove(tunnel.Key, out var removedTunnel))
            {
                try
                {
                    (removedTunnel as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing tunnel {tunnel.Key}");
                }
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;

        // Parse the path to determine what handler to use
        if (path.StartsWith("tunnel", StringComparison.OrdinalIgnoreCase))
        {
            await HandleTunnelConnectionAsync(context, cancellationToken);
        }
        else if (path.Equals("$status", StringComparison.OrdinalIgnoreCase))
        {
            await SendStatusPageAsync(context);
        }
        else
        {
            await HandleTunnelRequestAsync(context, path);
        }
    }

    private async Task HandleTunnelConnectionAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            await context.Response.Handle400("WebSocket connection required");
            return;
        }

        // Extract tunnel ID from query string or use default
        string? tunnelId = null;
        if (context.Request.QueryString["id"] != null)
        {
            tunnelId = context.Request.QueryString["id"];
        }

        if (_config.UseSingleTunnel)
        {
            if (!_tunnels.IsEmpty)
            {
                await context.Response.Handle400("Single tunnel mode enabled and there is one active tunnel");
                return;
            }
            tunnelId = Guid.NewGuid().ToString();
        }
        else
        {
            tunnelId ??= Guid.NewGuid().ToString();
        }

        // Create and register the tunnel
        IPGROKTunnel tunnel;
        try
        {
            tunnel = new WebSocketTunnel(context, tunnelId, _logger, _httpClientFactory, _config.ProxyPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to create tunnel: {tunnelId}");
            await context.Response.Handle400($"Failed to establish tunnel: {ex.Message}");
            return;
        }

        if (!_tunnels.TryAdd(tunnelId, tunnel))
        {
            await context.Response.Handle400($"Tunnel ID {tunnelId} is already in use");
            return;
        }

        _logger.LogInformation($"New tunnel registered: {tunnelId}");

        try
        {
            await tunnel.HandleTunnelMessages();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in tunnel {tunnelId}");
        }
        finally
        {
            _tunnels.TryRemove(tunnelId, out _);
            _logger.LogInformation($"Tunnel {tunnelId} removed");

            // Dispose if it implements IDisposable
            if (tunnel is IDisposable disposable)
            {
                try
                {
                    disposable.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing tunnel {tunnelId}");
                }
            }
        }
    }

    private async Task HandleTunnelRequestAsync(HttpListenerContext context, string path)
    {
        var segments = path.Split('/', 2);
        var tunnelId = segments[0];

        if (!_config.UseSingleTunnel && string.IsNullOrEmpty(tunnelId))
        {
            await SendStatusPageAsync(context);
            return;
        }

        // In single tunnel mode, use the only tunnel
        if (_config.UseSingleTunnel)
        {
            if (_tunnels.IsEmpty)
            {
                await context.Response.Handle400("No active tunnel");
                return;
            }
            tunnelId = _tunnels.First().Key;
        }

        if (_tunnels.TryGetValue(tunnelId, out var tunnel))
        {
            try
            {
                await tunnel.HandleRequest(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling request for tunnel {tunnelId}");
                await context.Response.Handle400($"Error handling request: {ex.Message}");
            }
        }
        else
        {
            await context.Response.Handle400($"No tunnel found for service: {tunnelId}");
        }
    }

    private async Task SendStatusPageAsync(HttpListenerContext context)
    {
        var html = new StringBuilder()
            .AppendLine("<!DOCTYPE html>")
            .AppendLine("<html>")
            .AppendLine("<head>")
            .AppendLine("    <title>PGrok - Active Tunnels</title>")
            .AppendLine("    <meta charset=\"utf-8\">")
            .AppendLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .AppendLine("    <style>")
            .AppendLine("        body { font-family: system-ui, -apple-system, sans-serif; margin: 0; padding: 20px; line-height: 1.6; }")
            .AppendLine("        .container { max-width: 1200px; margin: 0 auto; padding: 20px; }")
            .AppendLine("        h1 { color: #2563eb; margin-bottom: 1rem; }")
            .AppendLine("        .tunnel-list { list-style-type: none; padding: 0; }")
            .AppendLine("        .tunnel-item { padding: 16px; margin: 8px 0; background: #f3f4f6; border-radius: 8px; }")
            .AppendLine("        .tunnel-item.active { border-left: 4px solid #10b981; }")
            .AppendLine("        .tunnel-header { display: flex; justify-content: space-between; align-items: center; }")
            .AppendLine("        .tunnel-id { font-weight: bold; font-family: monospace; }")
            .AppendLine("        .tunnel-url { color: #4b5563; word-break: break-all; }")
            .AppendLine("        .tunnel-stats { margin-top: 8px; font-size: 0.9rem; color: #6b7280; }")
            .AppendLine("        .status-badge { display: inline-block; padding: 4px 8px; border-radius: 4px; font-size: 0.8rem; }")
            .AppendLine("        .status-active { background: #d1fae5; color: #047857; }")
            .AppendLine("        .no-tunnels { padding: 2rem; text-align: center; color: #6b7280; background: #f9fafb; border-radius: 8px; }")
            .AppendLine("        .mode-banner { padding: 8px 16px; background: #e0f2fe; border-radius: 6px; margin-bottom: 1rem; }")
            .AppendLine("    </style>")
            .AppendLine("</head>")
            .AppendLine("<body>")
            .AppendLine("    <div class=\"container\">");

        if (_config.UseSingleTunnel)
        {
            html.AppendLine("    <div class=\"mode-banner\">Single tunnel mode is activated</div>");
        }

        html.AppendLine("    <h1>PGrok Active Tunnels</h1>");

        if (_tunnels.IsEmpty)
        {
            html.AppendLine("    <div class=\"no-tunnels\">No active tunnels</div>");
        }
        else
        {
            html.AppendLine("    <ul class=\"tunnel-list\">");
            foreach (var (tunnelId, tunnel) in _tunnels)
            {
                var lastActivity = (tunnel as WebSocketTunnel)?.LastActivity ?? DateTime.UtcNow;
                var activityStatus = DateTime.UtcNow.Subtract(lastActivity).TotalMinutes < 5 ? "Active" : "Idle";
                var requestCount = (tunnel as WebSocketTunnel)?.RequestCount ?? 0;

                html.AppendLine($"        <li class=\"tunnel-item active\">")
                    .AppendLine($"            <div class=\"tunnel-header\">")
                    .AppendLine($"                <span class=\"tunnel-id\">{WebUtility.HtmlEncode(tunnelId)}</span>")
                    .AppendLine($"                <span class=\"status-badge status-active\">{activityStatus}</span>")
                    .AppendLine($"            </div>")
                    .AppendLine($"            <div class=\"tunnel-url\">http://{context.Request.Url?.Host}:{_config.Port}/{WebUtility.HtmlEncode(tunnelId)}/</div>")
                    .AppendLine($"            <div class=\"tunnel-stats\">")
                    .AppendLine($"                Requests: {requestCount} | Last activity: {lastActivity.ToString("yyyy-MM-dd HH:mm:ss")} UTC")
                    .AppendLine($"            </div>")
                    .AppendLine($"        </li>");
            }
            html.AppendLine("    </ul>");
        }

        html.AppendLine("    </div>")
            .AppendLine("</body>")
            .AppendLine("</html>");

        context.Response.ContentType = "text/html";
        await context.Response.HandleXXX(html.ToString(), 200, true);
    }

    public async Task StopAsync()
    {
        if (_cancellationTokenSource != null)
        {
            await _cancellationTokenSource.CancelAsync();

            if (_listenerTask != null)
            {
                try
                {
                    // Wait for the listener task to complete
                    await _listenerTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping listener task");
                }
                _listenerTask = null;
            }

            _cancellationTokenSource.Dispose();
            _cancellationTokenSource = null;
        }

        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        // Close all tunnels
        foreach (var (tunnelId, tunnel) in _tunnels.ToArray())
        {
            try
            {
                (tunnel as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error disposing tunnel {tunnelId}");
            }
        }

        _tunnels.Clear();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Stop the server and dispose resources
                StopAsync().GetAwaiter().GetResult();
                _listener.Close();
            }

            _disposed = true;
        }
    }
}