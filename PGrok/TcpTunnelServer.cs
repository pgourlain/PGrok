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
        _webSocketListener.Prefixes.Add($"http://{host}:{_webSocketPort}/");
        _webSocketListener.Start();
        _logger.LogInformation($"WebSocket server started on port {_webSocketPort}");

        _tcpListener.Start();
        _logger.LogInformation($"TCP listener started on port {_targetPort}");

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

                if (_clientWebSocket != null && _clientWebSocket.State == WebSocketState.Open)
                {
                    context.Response.StatusCode = 409;
                    context.Response.Close();
                    continue;
                }

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
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

                    foreach (var conn in _tcpConnections)
                    {
                        conn.Value.Close();
                    }
                    _tcpConnections.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling WebSocket connection");
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

                if (_clientWebSocket == null || _clientWebSocket.State != WebSocketState.Open)
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

    private async Task HandleTcpClientAsync(TcpClient tcpClient)
    {
        var connectionId = Guid.NewGuid().ToString();
        _tcpConnections[connectionId] = tcpClient;

        try
        {
            _logger.LogInformation($"New TCP connection: {connectionId}");

            // Envoyer le message d'initialisation au client
            var initMessage = new TunnelTcpMessage {
                Type = "init",
                ConnectionId = connectionId,
                Data = null
            };

            await SendWebSocketMessage(initMessage);

            using var stream = tcpClient.GetStream();
            var buffer = new byte[8192];

            // Tâche de lecture TCP
            var readTask = Task.Run(async () =>
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                    if (bytesRead == 0) break;

                    var message = new TunnelTcpMessage {
                        Type = "data",
                        ConnectionId = connectionId,
                        Data = Convert.ToBase64String(buffer, 0, bytesRead)
                    };

                    await SendWebSocketMessage(message);
                }
            });

            await readTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error handling TCP client {connectionId}");
        }
        finally
        {
            // Envoyer un message de fermeture au client
            var closeMessage = new TunnelTcpMessage {
                Type = "close",
                ConnectionId = connectionId,
                Data = null
            };

            await SendWebSocketMessage(closeMessage);

            _tcpConnections.Remove(connectionId);
            tcpClient.Close();
        }
    }
    private async Task ProcessWebSocketMessages(WebSocket webSocket)
    {
        var buffer = new byte[8192];

        while (webSocket.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
            try
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();

                do
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing WebSocket message");
                break;
            }
        }
    }

    private async Task HandleTunnelMessage(TunnelTcpMessage message)
    {
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