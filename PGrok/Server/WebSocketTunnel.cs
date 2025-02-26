using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace PGrok.Server;

public interface IPGROKTunnel : IDisposable
{
    Task HandleTunnelMessages();
    Task HandleRequest(HttpListenerContext context);
    DateTime LastActivity { get; }
    int RequestCount { get; }
}

class WebSocketTunnel : IPGROKTunnel, IDisposable
{
    private class TunnelConnection : IDisposable
    {
        public required WebSocket WebSocket { get; set; }
        public required CancellationTokenSource CancellationToken { get; set; }
        public ConcurrentDictionary<string, TaskCompletionSource<string>> ResponseWaiters { get; } =
            new ConcurrentDictionary<string, TaskCompletionSource<string>>();
        public ConcurrentDictionary<string, WebSocketConnection> ActiveWebSockets { get; } =
            new ConcurrentDictionary<string, WebSocketConnection>();
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Cancel all pending operations
            CancellationToken.Cancel();
            CancellationToken.Dispose();

            // Complete all waiters with cancellation
            foreach (var waiter in ResponseWaiters.Values)
            {
                waiter.TrySetCanceled();
            }
            ResponseWaiters.Clear();

            // Close all active WebSocket connections
            foreach (var connection in ActiveWebSockets.Values)
            {
                connection.Dispose();
            }
            ActiveWebSockets.Clear();

            // Close the WebSocket if still open
            if (WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Tunnel disposed",
                        System.Threading.CancellationToken.None
                    ).Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }
            WebSocket.Dispose();
        }
    }

    private class WebSocketConnection : IDisposable
    {
        public required string ConnectionId { get; init; }
        public required WebSocket ClientWebSocket { get; init; } // Browser to PGROK Server
        public required string TunnelId { get; init; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public CancellationTokenSource CancellationTokenSource { get; } = new CancellationTokenSource();
        public ArraySegment<byte> Buffer { get; } = new byte[8192];
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CancellationTokenSource.Cancel();
            CancellationTokenSource.Dispose();

            if (ClientWebSocket.State == WebSocketState.Open)
            {
                try
                {
                    ClientWebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection disposed",
                        CancellationToken.None
                    ).Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore errors during cleanup
                }
            }
            ClientWebSocket.Dispose();
        }
    }

    private readonly HttpListenerContext _tunnelContext;
    private readonly string _tunnelId;
    private readonly ILogger _logger;
    private readonly int _proxyPort;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly bool _debugMode;
    private readonly TunnelMetrics _metrics;
    private TunnelConnection? _tunnelConnection;
    private bool _disposed;

    // Add metrics tracking
    private class TunnelMetrics
    {
        public int TotalRequests { get; set; }
        public int SuccessfulRequests { get; set; }
        public int FailedRequests { get; set; }
        public int WebSocketConnections { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public ConcurrentDictionary<string, DateTime> EndpointHits { get; } = new();

        public void RecordRequest(string endpoint, bool success)
        {
            TotalRequests++;
            if (success) SuccessfulRequests++; else FailedRequests++;
            LastActivity = DateTime.UtcNow;
            EndpointHits[endpoint] = DateTime.UtcNow;
        }

        public void RecordWebSocketConnection()
        {
            WebSocketConnections++;
            LastActivity = DateTime.UtcNow;
        }
    }

    public DateTime LastActivity => _metrics.LastActivity;
    public int RequestCount => _metrics.TotalRequests;

    public WebSocketTunnel(HttpListenerContext context, string tunnelId, ILogger logger,
        IHttpClientFactory httpClientFactory, int proxyPort)
    {
        _tunnelContext = context;
        _tunnelId = tunnelId;
        _logger = logger;
        _proxyPort = proxyPort;
        _httpClientFactory = httpClientFactory;
        _debugMode = _logger.IsEnabled(LogLevel.Debug);
        _metrics = new TunnelMetrics();
        _disposed = false;
    }

    /// <summary>
    /// Handle tunnel connection between client and server (PGROK)
    /// </summary>
    public async Task HandleTunnelMessages()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebSocketTunnel));

        try
        {
            var wsContext = await _tunnelContext.AcceptWebSocketAsync(null);
            _tunnelConnection = new TunnelConnection {
                WebSocket = wsContext.WebSocket,
                CancellationToken = new CancellationTokenSource()
            };

            try
            {
                _logger.LogInformation($"PGROKClient is connected to tunnel: {_tunnelId}");
                await ProcessTunnelMessages(_tunnelId, _tunnelConnection);
            }
            finally
            {
                if (_tunnelConnection != null)
                {
                    try
                    {
                        var connection = _tunnelConnection;
                        _tunnelConnection = null;
                        connection.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing tunnel connection");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in tunnel connection: {_tunnelId}");
            throw;
        }
    }

    private async Task ProcessTunnelMessages(string tunnelId, TunnelConnection connection)
    {
        using var ms = new MemoryStream();
        var pingTimer = new System.Timers.Timer(30000); // 30 seconds

        pingTimer.Elapsed += async (sender, e) => {
            try
            {
                if (connection.WebSocket.State == WebSocketState.Open)
                {
                    await WebSocketHelpers.SendStringAsync(connection.WebSocket, "$ping$", connection.CancellationToken.Token);
                }
            }
            catch
            {
                // Ignore errors during ping
            }
        };

        pingTimer.Start();

        try
        {
            _logger.LogInformation($"Processing tunnel messages for: {tunnelId}");

            while (connection.WebSocket.State == WebSocketState.Open &&
                   !connection.CancellationToken.IsCancellationRequested)
            {
                var response = await WebSocketHelpers.ReceiveStringAsync(
                    connection.WebSocket,
                    ms,
                    connection.CancellationToken.Token);

                if (string.IsNullOrEmpty(response))
                {
                    continue;
                }

                // Update activity timestamp
                _metrics.LastActivity = DateTime.UtcNow;

                if (response == "$pong$")
                {
                    // Handle pong response
                    continue;
                }

                if (response == "$ping$")
                {
                    // Respond to ping with pong
                    await WebSocketHelpers.SendStringAsync(connection.WebSocket, "$pong$", connection.CancellationToken.Token);
                    continue;
                }

                await ProcessWebSocketMessage(response, connection);
            }
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpListenerException inner && inner.NativeErrorCode == 995)
        {
            // The I/O operation has been aborted - expected during shutdown
            _logger.LogDebug($"WebSocket operation aborted: {tunnelId}");
        }
        catch (OperationCanceledException) when (connection.CancellationToken.IsCancellationRequested)
        {
            // Expected when cancellation requested
            _logger.LogDebug($"Tunnel operation canceled: {tunnelId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing tunnel messages: {tunnelId}");

            // Signal error to all waiters
            foreach (var waiter in connection.ResponseWaiters.Values)
            {
                waiter.TrySetException(ex);
            }
        }
        finally
        {
            pingTimer.Stop();
            pingTimer.Dispose();

            // Close WebSocket if still open
            if (connection.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await connection.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Tunnel closed",
                        CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error closing WebSocket: {tunnelId}");
                }
            }
        }
    }

    private async Task ProcessWebSocketMessage(string message, TunnelConnection connection)
    {
        try
        {
            if (message.StartsWith("$dispatch$"))
            {
                // Handle dispatch messages for regular HTTP
                await ProcessDispatchMessage(message.AsSpan(10).ToString(), connection);
            }
            else if (message.StartsWith("$wsrelay$"))
            {
                // Handle WebSocket relay messages
                await ProcessWebSocketRelayMessage(message.AsSpan(9).ToString(), connection);
            }
            else
            {
                // Handle regular HTTP responses
                await ProcessHttpResponseMessage(message, connection);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message");
        }
    }

    private async Task ProcessDispatchMessage(string messageContent, TunnelConnection connection)
    {
        var request = JsonSerializer.Deserialize<TunnelRequest>(messageContent);
        if (request == null || string.IsNullOrWhiteSpace(request.Url))
        {
            _logger.LogWarning("Received invalid dispatch request");
            return;
        }

        var path = new Uri(request.Url).AbsolutePath.TrimStart('/') ?? string.Empty;
        var segments = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var serviceId = segments.Length > 0 ? segments[0] : string.Empty;

        try
        {
            var httpResponse = await HttpHelpers.ForwardRequestToLocalService(
                request,
                serviceId,
                $"http://{serviceId}:{_proxyPort}",
                _httpClientFactory.CreateClient(),
                _logger,
                "pgrok-server:");

            var httpResponseData = "$dispatchresponse$" + JsonSerializer.Serialize(httpResponse);
            await WebSocketHelpers.SendStringAsync(connection.WebSocket, httpResponseData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error forwarding dispatch request to {serviceId}");

            // Create and send error response
            var errorResponse = TunnelResponse.FromException(ex);
            errorResponse.RequestId = request.RequestId;
            var errorResponseData = "$dispatchresponse$" + JsonSerializer.Serialize(errorResponse);
            await WebSocketHelpers.SendStringAsync(connection.WebSocket, errorResponseData);
        }
    }

    private async Task ProcessWebSocketRelayMessage(string messageContent, TunnelConnection connection)
    {
        var relayData = JsonSerializer.Deserialize<WebSocketRelayMessage>(messageContent);

        if (relayData != null && !string.IsNullOrEmpty(relayData.ConnectionId))
        {
            if (connection.ActiveWebSockets.TryGetValue(relayData.ConnectionId, out var wsConn))
            {
                wsConn.LastActivity = DateTime.UtcNow;
                _metrics.LastActivity = DateTime.UtcNow;

                if (relayData.MessageType == WebSocketMessageType.Close)
                {
                    // Handle close message
                    try
                    {
                        await wsConn.ClientWebSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Connection closed by client",
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error closing WebSocket connection: {relayData.ConnectionId}");
                    }

                    connection.ActiveWebSockets.TryRemove(relayData.ConnectionId, out _);
                    _logger.LogInformation($"WebSocket connection closed: {relayData.ConnectionId}");
                }
                else if (relayData.Data != null)
                {
                    // Forward data to client
                    try
                    {
                        await wsConn.ClientWebSocket.SendAsync(
                            relayData.Data,
                            relayData.MessageType,
                            relayData.EndOfMessage,
                            CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error sending data to WebSocket: {relayData.ConnectionId}");

                        // Close the connection on error
                        try
                        {
                            await wsConn.ClientWebSocket.CloseAsync(
                                WebSocketCloseStatus.InternalServerError,
                                "Error sending data",
                                CancellationToken.None);
                        }
                        catch
                        {
                            // Ignore errors during close
                        }

                        connection.ActiveWebSockets.TryRemove(relayData.ConnectionId, out _);
                    }
                }
            }
            else
            {
                _logger.LogWarning($"Received WebSocket relay for unknown connection: {relayData.ConnectionId}");
            }
        }
    }

    private Task ProcessHttpResponseMessage(string message, TunnelConnection connection)
    {
        var tunnelResponse = JsonSerializer.Deserialize<TunnelResponse>(message);
        var requestId = tunnelResponse?.RequestId;

        if (string.IsNullOrEmpty(requestId))
        {
            _logger.LogWarning("Received response without request ID");
            return Task.CompletedTask;
        }

        if (tunnelResponse?.WebSocketRequest == true && tunnelResponse.WebSocketAction == "upgrade" &&
            connection.ResponseWaiters.TryGetValue(requestId, out var upgradeWaiter))
        {
            // This response indicates the target supports WebSocket and we should upgrade
            upgradeWaiter.TrySetResult(message);
        }
        else if (connection.ResponseWaiters.TryGetValue(requestId, out var waiter))
        {
            // Set the result for regular HTTP requests
            waiter.TrySetResult(message);
        }
        else
        {
            _logger.LogWarning($"Received response for unknown request ID: {requestId}");
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handle request from "public" interface
    /// </summary>
    public async Task HandleRequest(HttpListenerContext context)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WebSocketTunnel));

        // Update metrics
        _metrics.LastActivity = DateTime.UtcNow;
        var endpoint = context.Request.Url?.AbsolutePath ?? "/";
        var success = false;

        try
        {
            if (_tunnelConnection == null)
            {
                await context.HandleXXX(new {
                    error = "Tunnel not available",
                    message = "The tunnel client is not available",
                    tunnelId = _tunnelId
                }, 503);
                return;
            }

            if (_debugMode)
            {
                LogRequestInfos(context);
            }

            // Check if this is a WebSocket request
            if (context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketRequest(context);
                success = true;
                _metrics.RecordRequest(endpoint, success);
                return;
            }

            // Generate a unique request ID
            string requestId = Guid.NewGuid().ToString();

            var request = new TunnelRequest {
                RequestId = requestId,
                Method = context.Request.HttpMethod,
                Url = context.Request.Url?.ToString() ?? string.Empty,
                Headers = HttpHelpers.GetHeaders(context.Request),
                Body = await HttpHelpers.GetRequestBodyAsync(context.Request)
            };

            // Check for Blazor-specific headers that might indicate this is part of a Blazor app
            bool isBlazorRequest = IsBlazorRequest(context.Request);
            if (isBlazorRequest)
            {
                request.IsBlazorRequest = true;
            }

            // Create a new waiter for this specific request
            var responseWaiter = new TaskCompletionSource<string>();
            _tunnelConnection.ResponseWaiters.TryAdd(requestId, responseWaiter);

            var requestJson = JsonSerializer.Serialize(request);
            await WebSocketHelpers.SendStringAsync(_tunnelConnection.WebSocket, requestJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                var responseJson = await responseWaiter.Task.WaitAsync(cts.Token);
                var response = JsonSerializer.Deserialize<TunnelResponse>(responseJson);

                // If this is a WebSocket upgrade response (for Blazor), handle it specially
                if (response?.WebSocketRequest == true && response.WebSocketAction == "upgrade")
                {
                    // Switch to WebSocket protocol
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    await HandleWebSocketConnection(wsContext, requestId);
                    success = true;
                }
                else
                {
                    await context.Response.HandleResponse(response, isBlazorRequest);
                    success = response?.StatusCode < 400;
                }
            }
            catch (OperationCanceledException)
            {
                await context.HandleXXX(new {
                    error = "Gateway Timeout",
                    message = "The tunnel client did not respond in time",
                    tunnelId = _tunnelId
                }, 504);
            }
            finally
            {
                // Clean up the waiter
                _tunnelConnection.ResponseWaiters.TryRemove(requestId, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling HTTP request: {endpoint}");

            await context.HandleXXX(new {
                error = "Internal Server Error",
                message = "An error occurred while processing the request",
                details = ex.Message
            }, 500);
        }
        finally
        {
            _metrics.RecordRequest(endpoint, success);

            if (!context.Request.IsWebSocketRequest)
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Ignore errors closing the response
                }
            }
        }
    }

    /// <summary>
    /// Handle a WebSocket connection for bidirectional communication (like Blazor SignalR)
    /// </summary>
    private async Task HandleWebSocketRequest(HttpListenerContext context)
    {
        if (_tunnelConnection == null)
        {
            context.Response.StatusCode = 503;
            context.Response.Close();
            return;
        }

        string connectionId = Guid.NewGuid().ToString();
        string requestId = $"ws-{connectionId}";

        // Create request to notify the tunnel client about the WebSocket connection
        var request = new TunnelRequest {
            RequestId = requestId,
            Method = "GET",
            Url = context.Request.Url?.ToString() ?? string.Empty,
            Headers = HttpHelpers.GetHeaders(context.Request),
            IsWebSocketRequest = true
        };

        // Check if this is a Blazor SignalR connection
        bool isBlazorSignalR = IsBlazorSignalRRequest(context.Request);
        if (isBlazorSignalR)
        {
            request.IsBlazorRequest = true;
        }

        // Create a waiter for the WebSocket upgrade response
        var responseWaiter = new TaskCompletionSource<string>();
        _tunnelConnection.ResponseWaiters.TryAdd(requestId, responseWaiter);

        // Send the WebSocket request to the tunnel client
        var requestJson = JsonSerializer.Serialize(request);
        await WebSocketHelpers.SendStringAsync(_tunnelConnection.WebSocket, requestJson);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            // Wait for the response from the tunnel client
            var responseJson = await responseWaiter.Task.WaitAsync(cts.Token);
            var response = JsonSerializer.Deserialize<TunnelResponse>(responseJson);

            if (response?.WebSocketRequest == true && response.WebSocketAction == "upgrade")
            {
                // The tunnel client accepted the WebSocket connection
                var wsContext = await context.AcceptWebSocketAsync(null);
                _metrics.RecordWebSocketConnection();
                await HandleWebSocketConnection(wsContext, connectionId);
            }
            else
            {
                // The tunnel client rejected the WebSocket connection
                context.Response.StatusCode = 400;
                context.Response.Close();
            }
        }
        catch (OperationCanceledException)
        {
            context.Response.StatusCode = 504;
            context.Response.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling WebSocket request: {requestId}");
            context.Response.StatusCode = 500;
            context.Response.Close();
        }
        finally
        {
            _tunnelConnection.ResponseWaiters.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Handle an established WebSocket connection
    /// </summary>
    private async Task HandleWebSocketConnection(HttpListenerWebSocketContext wsContext, string connectionId)
    {
        if (_tunnelConnection == null)
        {
            await wsContext.WebSocket.CloseAsync(
                WebSocketCloseStatus.EndpointUnavailable,
                "Tunnel not available",
                CancellationToken.None);
            return;
        }

        // Create a new WebSocket connection
        var wsConn = new WebSocketConnection {
            ConnectionId = connectionId,
            ClientWebSocket = wsContext.WebSocket,
            TunnelId = _tunnelId
        };

        // Add it to the active connections
        _tunnelConnection.ActiveWebSockets.TryAdd(connectionId, wsConn);

        try
        {
            _logger.LogInformation($"WebSocket connection established: {connectionId}");

            // Start receiving messages from the client
            var ct = wsConn.CancellationTokenSource.Token;

            // Continue receiving until the connection is closed
            while (wsConn.ClientWebSocket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // Receive a message from the client
                WebSocketReceiveResult result;
                try
                {
                    result = await wsConn.ClientWebSocket.ReceiveAsync(wsConn.Buffer, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }

                // Update last activity
                wsConn.LastActivity = DateTime.UtcNow;
                _metrics.LastActivity = DateTime.UtcNow;

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Client is closing the connection
                    _logger.LogInformation($"WebSocket client requested close: {connectionId}");

                    // Notify the tunnel client
                    var closeMessage = new WebSocketRelayMessage {
                        ConnectionId = connectionId,
                        MessageType = WebSocketMessageType.Close
                    };

                    await WebSocketHelpers.SendStringAsync(
                        _tunnelConnection.WebSocket,
                        "$wsrelay$" + JsonSerializer.Serialize(closeMessage));

                    break;
                }

                // Relay the message to the tunnel client
                var relayMessage = new WebSocketRelayMessage {
                    ConnectionId = connectionId,
                    MessageType = result.MessageType,
                    EndOfMessage = result.EndOfMessage,
                    Data = new ArraySegment<byte>(wsConn.Buffer.Array!, wsConn.Buffer.Offset, result.Count).ToArray()
                };

                await WebSocketHelpers.SendStringAsync(
                    _tunnelConnection.WebSocket,
                    "$wsrelay$" + JsonSerializer.Serialize(relayMessage));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error in WebSocket connection: {connectionId}");
        }
        finally
        {
            // Remove from active connections
            _tunnelConnection.ActiveWebSockets.TryRemove(connectionId, out var removedConn);

            // Ensure the WebSocket is properly disposed
            if (removedConn != null)
            {
                try
                {
                    removedConn.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error disposing WebSocket connection: {connectionId}");
                }
            }

            _logger.LogInformation($"WebSocket connection closed: {connectionId}");
        }
    }

    /// <summary>
    /// Check if a request appears to be for a Blazor application
    /// </summary>
    private bool IsBlazorRequest(HttpListenerRequest request)
    {
        // Check for Blazor-specific paths or headers
        string path = request.Url?.AbsolutePath ?? string.Empty;

        // Check for Blazor resources
        if (path.EndsWith(".blazor.js", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".blazor.webassembly.js", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/_framework/blazor.", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/_blazor/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for Blazor-specific headers
        string acceptHeader = request.Headers["Accept"] ?? string.Empty;
        if (acceptHeader.Contains("text/x-blazor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a request appears to be a Blazor SignalR connection
    /// </summary>
    private bool IsBlazorSignalRRequest(HttpListenerRequest request)
    {
        // Check for SignalR negotiation or connection URLs
        string path = request.Url?.AbsolutePath ?? string.Empty;

        if (path.Contains("/_blazor/negotiate", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/_blazor/connect", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for SignalR-specific headers
        string connectionHeader = request.Headers["Connection"] ?? string.Empty;
        string upgradeHeader = request.Headers["Upgrade"] ?? string.Empty;

        if (connectionHeader.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) &&
            upgradeHeader.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            // This is a WebSocket upgrade request, check if it's for SignalR
            return path.Contains("/_blazor", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void LogRequestInfos(HttpListenerContext context)
    {
        using var logScope = _logger.BeginScope(new Dictionary<string, object> {
            ["TunnelId"] = _tunnelId,
            ["RemoteEndPoint"] = context.Request.RemoteEndPoint?.ToString() ?? "unknown",
            ["RequestMethod"] = context.Request.HttpMethod,
            ["RequestUrl"] = context.Request.Url?.ToString() ?? "unknown"
        });

        _logger.LogDebug("----------------------------------------------");
        _logger.LogDebug($"Request from {context.Request.RemoteEndPoint}");
        _logger.LogDebug(context.Request.IsWebSocketRequest ? "WebSocket request" : "HTTP request");
        _logger.LogDebug($"Request method: {context.Request.HttpMethod}");
        _logger.LogDebug($"Request URL: {context.Request.Url}");

        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace($"Request headers:");
            foreach (var key in context.Request.Headers.AllKeys)
            {
                _logger.LogTrace($"{key}: {context.Request.Headers[key]}");
            }
        }
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
                // Dispose managed resources
                if (_tunnelConnection != null)
                {
                    try
                    {
                        _tunnelConnection.Dispose();
                        _tunnelConnection = null;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing tunnel connection");
                    }
                }
            }

            _disposed = true;
        }
    }
}