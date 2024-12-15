using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Server;

public class TcpTunnelServer
{
    private readonly HttpListener _webSocketListener;
    private readonly TcpListener _tcpListener;
    private readonly ILogger _logger;
    private readonly int _webSocketPort;
    private readonly int _targetPort;
    private readonly bool _useLocalhost;
    private WebSocket? _clientWebSocket;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, TcpClient> _tcpConnections;

    public TcpTunnelServer(ILogger logger, int webSocketPort, int targetPort, bool useLocalhost = false)
    {
        _logger = logger;
        _webSocketPort = webSocketPort;
        _targetPort = targetPort;
        _useLocalhost = useLocalhost;
        _webSocketListener = new HttpListener();
        _tcpListener = new TcpListener(_useLocalhost ? IPAddress.Loopback : IPAddress.Any, targetPort);
        _cts = new CancellationTokenSource();
        _tcpConnections = new Dictionary<string, TcpClient>();
    }

    public async Task Start()
    {
        var host = _useLocalhost ? "localhost" : "+";
        // Start WebSocket listener for tunnel client connection
        _webSocketListener.Prefixes.Add($"http://{host}:{_webSocketPort}/");
        _webSocketListener.Start();
        _logger.LogInformation($"WebSocket server started on port {_webSocketPort}");

        // Start TCP listener for incoming connections to forward
        _tcpListener.Start();
        _logger.LogInformation($"TCP listener started on port {_targetPort}");

        // Start both listening tasks
        var webSocketTask = HandleWebSocketConnections();
        var tcpTask = HandleTcpConnections();
        _logger.LogInformation("Ready to accept connections");
        await Task.WhenAll(webSocketTask, tcpTask);
    }

    private async Task HandleWebSocketConnections()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _webSocketListener.GetContextAsync();

                // If there's already a client connected, reject new connections
                if (_clientWebSocket != null &&
                    _clientWebSocket.State == WebSocketState.Open)
                {
                    context.Response.StatusCode = 409; // Conflict
                    context.Response.Close();
                    continue;
                }

                await HandleWebSocketClientAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting WebSocket connection");
            }
        }
    }

    private async Task HandleTcpConnections()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _tcpListener.AcceptTcpClientAsync(_cts.Token);

                // Only accept TCP connections if we have a connected client
                if (_clientWebSocket == null ||
                    _clientWebSocket.State != WebSocketState.Open)
                {
                    _logger.LogWarning("No tunnel client connected. Rejecting TCP connection.");
                    tcpClient.Close();
                    continue;
                }

                _ = HandleTcpClientAsync(tcpClient);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting TCP connection");
            }
        }
    }

    private async Task HandleWebSocketClientAsync(HttpListenerContext context)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null);
        _clientWebSocket = wsContext.WebSocket;
        _logger.LogInformation("Tunnel client connected");

        try
        {
            await ProcessWebSocketMessages(_clientWebSocket);
        }
        finally
        {
            _clientWebSocket = null;
            _logger.LogInformation("Tunnel client disconnected");

            // Clean up any remaining TCP connections
            foreach (var conn in _tcpConnections)
            {
                conn.Value.Close();
            }
            _tcpConnections.Clear();
        }
    }

    private async Task HandleTcpClientAsync(TcpClient tcpClient)
    {
        var connectionId = Guid.NewGuid().ToString();
        _tcpConnections[connectionId] = tcpClient;

        try
        {
            _logger.LogInformation($"New TCP connection: {connectionId}");

            using var stream = tcpClient.GetStream();
            // Configuration de la socket pour une meilleure performance
            tcpClient.NoDelay = true;  // Désactive l'algorithme de Nagle

            var buffer = new byte[65536];

            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0)
                {
                    _logger.LogDebug($"Connection {connectionId} closed by client");
                    break;
                }

                var message = new TunnelTcpMessage {
                    Type = "data",
                    ConnectionId = connectionId,
                    Data = Convert.ToBase64String(buffer, 0, bytesRead)
                };

                await SendWebSocketMessage(message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling TCP client {connectionId}");
        }
        finally
        {
            _tcpConnections.Remove(connectionId);
            tcpClient.Close();
            _logger.LogInformation($"Connection {connectionId} closed");
        }
    }

    private async Task ProcessWebSocketMessages(WebSocket webSocket)
    {
        var buffer = new byte[4096];

        while (webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        _cts.Token);
                    await ms.WriteAsync(buffer.AsMemory(0, result.Count));
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Position = 0;
                    using var reader = new StreamReader(ms);
                    var messageText = await reader.ReadToEndAsync();
                    var message = JsonSerializer.Deserialize<TunnelTcpMessage>(messageText);

                    if (message != null)
                    {
                        await HandleTunnelMessage(message);
                    }
                }
            }
            catch (WebSocketException)
            {
                break;
            }
        }
    }

    private async Task HandleTunnelMessage(TunnelTcpMessage message)
    {
        // Handle responses from tunnel client
        if (message.Type == "data" && !string.IsNullOrEmpty(message.ConnectionId))
        {
            if (_tcpConnections.TryGetValue(message.ConnectionId, out var tcpClient))
            {
                var data = Convert.FromBase64String(message.Data ?? string.Empty);
                var stream = tcpClient.GetStream();
                await stream.WriteAsync(data);
            }
        }
    }

    private async Task SendWebSocketMessage(TunnelTcpMessage message)
    {
        if (_clientWebSocket?.State != WebSocketState.Open)
        {
            _logger.LogWarning("Tunnel client not connected");
            return;
        }

        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await WebSocketHelpers.SendStringAsync(_clientWebSocket, json, _cts.Token);
    }

    public async Task Stop()
    {
        _cts.Cancel();

        if (_clientWebSocket?.State == WebSocketState.Open)
        {
            await _clientWebSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Server shutting down",
                CancellationToken.None);
        }

        foreach (var client in _tcpConnections.Values)
        {
            client.Close();
        }
        _tcpConnections.Clear();

        _webSocketListener.Stop();
        _tcpListener.Stop();
    }
}