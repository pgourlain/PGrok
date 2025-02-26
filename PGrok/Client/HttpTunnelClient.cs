using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Client;

public class HttpTunnelClient : IAsyncDisposable
{
    private readonly string _serverUrl;
    private readonly string _tunnelId;
    private readonly ILogger _logger;
    private readonly string _localUrl;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cts;
    private readonly int? _proxyPort;
    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, HttpListenerContext> _currentRequests = new();
    private ClientWebSocket? _currentWS = null;
    private readonly ConcurrentDictionary<string, ClientWebSocket> _webSocketConnections = new();
    private Task? _connectionTask;
    private Task? _httpProxyTask;
    private readonly ReconnectionPolicy _reconnectionPolicy;
    private readonly ClientMetrics _metrics = new();
    private bool _isDisposed;

    // Class for tracking client metrics
    private class ClientMetrics
    {
        private int _TotalRequests;
        public int TotalRequests => _TotalRequests;
        private int _SuccessfulRequests;
        public int SuccessfulRequests => _SuccessfulRequests;

        private int _FailedRequests;
        public int FailedRequests => _FailedRequests;

        private int _Reconnections;
        public int Reconnections => _Reconnections;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public ConcurrentDictionary<string, int> EndpointHits { get; } = new();

        public void RecordRequest(string endpoint, bool success)
        {
            Interlocked.Increment(ref _TotalRequests);
            if (success) Interlocked.Increment(ref _SuccessfulRequests);
            else Interlocked.Increment(ref _FailedRequests);
            LastActivity = DateTime.UtcNow;

            EndpointHits.AddOrUpdate(endpoint, 1, (_, count) => count + 1);
        }

        public void RecordReconnection()
        {
            Interlocked.Increment(ref _Reconnections);
        }
    }

    // Class for handling reconnection logic with exponential backoff
    private class ReconnectionPolicy
    {
        private readonly TimeSpan _initialDelay;
        private readonly TimeSpan _maxDelay;
        private readonly double _backoffFactor;
        private readonly int _maxAttempts;
        private int _attemptCount;
        private TimeSpan _currentDelay;

        public ReconnectionPolicy(
            TimeSpan initialDelay,
            TimeSpan maxDelay,
            double backoffFactor = 2.0,
            int maxAttempts = int.MaxValue)
        {
            _initialDelay = initialDelay;
            _maxDelay = maxDelay;
            _backoffFactor = backoffFactor;
            _maxAttempts = maxAttempts;
            Reset();
        }

        public void Reset()
        {
            _attemptCount = 0;
            _currentDelay = _initialDelay;
        }

        public async Task<bool> WaitForNextAttemptAsync(CancellationToken cancellationToken)
        {
            _attemptCount++;

            if (_attemptCount > _maxAttempts)
            {
                return false;
            }

            try
            {
                await Task.Delay(_currentDelay, cancellationToken);

                // Increase delay with jitter for next attempt
                var nextDelay = TimeSpan.FromMilliseconds(
                    _currentDelay.TotalMilliseconds * _backoffFactor * (0.8 + 0.4 * new Random().NextDouble()));

                _currentDelay = nextDelay > _maxDelay ? _maxDelay : nextDelay;

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        public int AttemptCount => _attemptCount;
    }

    public HttpTunnelClient(string serverUrl, string tunnelId, string localUrl, int? proxyPort, ILogger logger)
    {
        _serverUrl = serverUrl;
        _tunnelId = tunnelId;
        _logger = logger;
        _localUrl = localUrl.TrimEnd('/');
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _proxyPort = proxyPort;
        _listener = new HttpListener();
        _cts = new CancellationTokenSource();
        _reconnectionPolicy = new ReconnectionPolicy(
            initialDelay: TimeSpan.FromSeconds(1),
            maxDelay: TimeSpan.FromMinutes(2),
            backoffFactor: 1.5,
            maxAttempts: 100
        );
        _isDisposed = false;
    }

    public Task StartAsync()
    {
        if (_connectionTask != null)
        {
            throw new InvalidOperationException("Client is already running");
        }

        _logger.LogInformation($"Starting tunnel client for service {_tunnelId}");
        _logger.LogInformation($"Tunnel Server: {_serverUrl}");
        _logger.LogInformation($"Forward http calls to: {_localUrl}");

        if (_proxyPort.HasValue)
        {
            _logger.LogInformation($"Reverse proxy mode is activated on http://localhost:{_proxyPort}");
            _logger.LogInformation($"- http://localhost:{_proxyPort}/[service]/.... make an http call on tunnel server side on specified service. This call will be translate like this http://service:[sererReverseProxyPort]/...");

            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{_proxyPort}/");
            _listener.Start();

            // Start HTTP proxy task
            _httpProxyTask = Task.Run(ProcessHttpProxyCall);
        }

        // Start connection task
        _connectionTask = Task.Run(ConnectionLoop);
        return _connectionTask;
    }

    private async Task ConnectionLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndProcess();
                // If we get here without exception, reset the reconnection policy
                _reconnectionPolicy.Reset();
            }
            catch (Exception ex)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning(ex, $"Connection error (attempt {_reconnectionPolicy.AttemptCount}): {ex.Message}");

                // Wait according to the reconnection policy
                if (!await _reconnectionPolicy.WaitForNextAttemptAsync(_cts.Token))
                {
                    _logger.LogError("Maximum reconnection attempts reached. Giving up.");
                    break;
                }

                _metrics.RecordReconnection();
                _logger.LogInformation($"Attempting to reconnect... (attempt {_reconnectionPolicy.AttemptCount})");
            }
        }
    }

    private async Task ConnectAndProcess()
    {
        using var ws = new ClientWebSocket();
        // Set reasonable timeouts
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // Add custom headers if needed
        ws.Options.SetRequestHeader("X-PGrok-Client-Version", "2.0");
        ws.Options.SetRequestHeader("X-PGrok-Tunnel-ID", _tunnelId);

        var wsUrl = $"{_serverUrl.Replace("https://", "wss://").Replace("http://", "ws://")}/tunnel?id={_tunnelId}";

        _logger.LogInformation($"Connecting to {wsUrl}...");

        try
        {
            await ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to connect to tunnel server: {ex.Message}", ex);
        }

        _logger.LogInformation("Connected successfully!");

        // Set the current WebSocket for the proxy to use
        Interlocked.Exchange(ref _currentWS, ws);

        // Process messages until the connection is closed
        await ProcessMessagesFromPGRokServer(ws);
    }

    private async Task ProcessMessagesFromPGRokServer(ClientWebSocket ws)
    {
        using var ms = new MemoryStream();
        using var pingTimer = new System.Timers.Timer(30000); // 30-second ping interval

        // Set up ping timer
        pingTimer.Elapsed += async (sender, e) => {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await WebSocketHelpers.SendStringAsync(ws, "$ping$", _cts.Token);
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
            _logger.LogInformation($"Processing tunnel messages for websocket client");

            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                string? message;
                try
                {
                    message = await WebSocketHelpers.ReceiveStringAsync(ws, ms, _cts.Token);
                }
                catch (WebSocketException ex)
                {
                    if (ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                    {
                        throw new Exception($"Connection closed by server: {ws.CloseStatusDescription}");
                    }

                    throw new Exception($"WebSocket error: {ex.Message}", ex);
                }

                if (string.IsNullOrEmpty(message))
                {
                    continue;
                }

                // Update activity timestamp
                _metrics.LastActivity = DateTime.UtcNow;

                if (message == "$ping$")
                {
                    // Respond with pong
                    await WebSocketHelpers.SendStringAsync(ws, "$pong$", _cts.Token);
                    continue;
                }

                if (message == "$pong$")
                {
                    // Ping response received, do nothing
                    continue;
                }

                await ProcessWebSocketMessage(message, ws);
            }
        }
        finally
        {
            pingTimer.Stop();

            // Clear the current WebSocket reference
            if (ReferenceEquals(_currentWS, ws))
            {
                Interlocked.Exchange(ref _currentWS, null);
            }

            // Close all active WebSocket connections
            await CloseAllWebSocketConnections();

            // Complete all pending requests with an error
            await CompletePendingRequestsWithError();
        }
    }

    private async Task CloseAllWebSocketConnections()
    {
        foreach (var (connectionId, connection) in _webSocketConnections.ToArray())
        {
            try
            {
                if (connection.State == WebSocketState.Open)
                {
                    await connection.CloseAsync(
                        WebSocketCloseStatus.EndpointUnavailable,
                        "Tunnel connection closed",
                        CancellationToken.None
                    );
                }

                connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error closing WebSocket connection {connectionId}");
            }
        }

        _webSocketConnections.Clear();
    }

    private async Task CompletePendingRequestsWithError()
    {
        foreach (var (requestId, context) in _currentRequests.ToArray())
        {
            try
            {
                _currentRequests.TryRemove(requestId, out _);

                await context.Response.HandleXXX(
                    JsonSerializer.Serialize(new {
                        error = "Tunnel Disconnected",
                        message = "The tunnel connection was lost while processing your request"
                    }),
                    503,
                    true
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error completing pending request {requestId}");
            }
        }
    }

    private async Task ProcessWebSocketMessage(string message, ClientWebSocket ws)
    {
        try
        {
            if (message.StartsWith("$dispatchresponse$"))
            {
                // Handle dispatch responses
                await ProcessDispatchResponse(message.AsSpan(18).ToString());
            }
            else
            {
                // Try to deserialize as a TunnelRequest
                var request = JsonSerializer.Deserialize<TunnelRequest>(message);

                if (request == null)
                {
                    _logger.LogWarning("Received invalid request");
                    return;
                }

                // Regular HTTP request handling
                await ProcessHttpRequest(request, ws);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WebSocket message");
        }
    }

    private async Task ProcessDispatchResponse(string message)
    {
        var dispatchResponse = JsonSerializer.Deserialize<TunnelResponse>(message);
        if (dispatchResponse == null)
        {
            _logger.LogWarning("Received invalid dispatch response");
            return;
        }

        if (dispatchResponse.RequestId != null && _currentRequests.TryRemove(dispatchResponse.RequestId, out var context))
        {
            try
            {
                await context.Response.HandleResponse(dispatchResponse, false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error handling dispatch response for request {dispatchResponse.RequestId}");
            }
        }
        else
        {
            _logger.LogWarning($"Received dispatch response for unknown request: {dispatchResponse.RequestId}");
        }
    }

    private async Task ProcessHttpRequest(TunnelRequest request, ClientWebSocket ws)
    {
        if (string.IsNullOrEmpty(request.RequestId))
        {
            _logger.LogWarning("Received request without ID");
            return;
        }

        string endpoint = new Uri(request.Url).AbsolutePath;
        bool success = false;

        try
        {
            _logger.LogInformation($"Processing HTTP request: {request.Method} {request.Url}");

            var response = await HttpHelpers.ForwardRequestToLocalService(
                request,
                _tunnelId,
                _localUrl,
                _httpClient,
                _logger);

            response.RequestId = request.RequestId;

            var responseJson = JsonSerializer.Serialize(response);
            await WebSocketHelpers.SendStringAsync(ws, responseJson, _cts.Token);

            success = response.StatusCode < 400;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing HTTP request: {request.Url}");

            var errorResponse = TunnelResponse.FromException(ex);
            errorResponse.RequestId = request.RequestId;

            var errorJson = JsonSerializer.Serialize(errorResponse);
            await WebSocketHelpers.SendStringAsync(ws, errorJson, _cts.Token);
        }
        finally
        {
            _metrics.RecordRequest(endpoint, success);
        }
    }

    private async Task ProcessHttpProxyCall()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (_cts.Token.IsCancellationRequested)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting proxy connection");
                continue;
            }

            _ = HandleProxyContextAsync(context);
        }
    }

    private async Task HandleProxyContextAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
        var segments = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || string.IsNullOrEmpty(segments[0]))
        {
            await context.Response.Handle400("Service to proxy is required.");
            return;
        }

        var serviceId = segments[0];
        var currentWs = _currentWS;

        if (currentWs == null || currentWs.State != WebSocketState.Open)
        {
            await context.Response.HandleXXX("Tunnel Service Unavailable", 503, true);
            return;
        }

        _logger.LogInformation($"Proxy call to service: {serviceId}{(segments.Length > 1 ? ("/" + segments[1]) : "")}");

        var request = new TunnelRequest {
            RequestId = Guid.NewGuid().ToString(),
            Method = context.Request.HttpMethod,
            Url = context.Request.Url?.ToString() ?? string.Empty,
            Headers = HttpHelpers.GetHeaders(context.Request),
            Body = await HttpHelpers.GetRequestBodyAsync(context.Request)
        };

        _currentRequests[request.RequestId!] = context;

        var requestJson = "$dispatch$" + JsonSerializer.Serialize(request);

        try
        {
            await WebSocketHelpers.SendStringAsync(currentWs, requestJson, _cts.Token);
        }
        catch (Exception ex)
        {
            _currentRequests.TryRemove(request.RequestId!, out _);
            _logger.LogError(ex, "Error sending proxy request");
            await context.Response.HandleXXX("Error sending request to tunnel server", 500, true);
        }
    }

    public async Task StopAsync()
    {
        if (_cts.IsCancellationRequested)
        {
            return;
        }

        _logger.LogInformation("Stopping tunnel client...");

        // Cancel the token to stop all operations
        await _cts.CancelAsync();

        // Wait for tasks to complete
        if (_connectionTask != null)
        {
            try
            {
                await _connectionTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for connection task to complete");
            }
        }

        if (_httpProxyTask != null)
        {
            try
            {
                await _httpProxyTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error waiting for HTTP proxy task to complete");
            }
        }

        // Stop the listener if it's running
        if (_listener.IsListening)
        {
            _listener.Stop();
        }

        // Close and dispose all WebSocket connections
        await CloseAllWebSocketConnections();

        // Complete all pending requests
        await CompletePendingRequestsWithError();

        _logger.LogInformation("Tunnel client stopped");
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        // Stop the client
        await StopAsync();

        // Dispose resources
        _cts.Dispose();
        _httpClient.Dispose();

        GC.SuppressFinalize(this);
    }
}