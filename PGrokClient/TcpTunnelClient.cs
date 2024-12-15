using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Client;

public class TcpTunnelClient
{
    private readonly string _serverUrl;
    private readonly string _tunnelId;
    private readonly string _localHost;
    private readonly int _localPort;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts;
    private readonly Dictionary<string, TcpClient> _connections;

    public TcpTunnelClient(
        string serverUrl,
        string tunnelId,
        string localHost,
        int localPort,
        ILogger logger)
    {
        _serverUrl = serverUrl;
        _tunnelId = tunnelId;
        _localHost = localHost;
        _localPort = localPort;
        _logger = logger;
        _cts = new CancellationTokenSource();
        _connections = new Dictionary<string, TcpClient>();
    }

    public async Task Start()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await ConnectAndProcess();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Connection error: {ex.Message}");
                _logger.LogWarning("Retrying in 5 seconds...");
                await Task.Delay(5000, _cts.Token);
            }
        }
    }

    private async Task ConnectAndProcess()
    {

        using var ws = new ClientWebSocket();
        var wsUrl = $"{_serverUrl.Replace("https://", "wss://").Replace("http://", "ws://")}/tunnel?id={_tunnelId}";

        _logger.LogInformation($"Connecting to {wsUrl}...");

        await ws.ConnectAsync(new Uri(wsUrl), _cts.Token);
        _logger.LogInformation("Connected successfully!");

        var receiveTask = ReceiveWebSocketMessages(ws);
        var heartbeatTask = SendHeartbeats(ws);

        await Task.WhenAny(receiveTask, heartbeatTask);
    }

    private async Task SendHeartbeats(WebSocket ws)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var message = new TunnelTcpMessage {
                    Type = "control",
                    ConnectionId = "heartbeat"
                };

                await SendWebSocketMessage(ws, message);
                await Task.Delay(30000, _cts.Token); // Heartbeat every 30 seconds
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReceiveWebSocketMessages(WebSocket ws)
    {
        var buffer = new byte[4096];

        try
        {
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {

                var message = await WebSocketHelpers.ReceiveStringAsync(ws, _cts.Token);
                var tcpmessage = JsonSerializer.Deserialize<TunnelTcpMessage>(message);

                if (message != null)
                {
                    await HandleTunnelMessage(ws, tcpmessage);
                }
            }
        }
        catch (WebSocketException ex) 
        {
            _logger.LogError($"WebSocket error : {ex.Message}");
        }
    }

    private async Task HandleTunnelMessage(WebSocket ws, TunnelTcpMessage message)
    {
        switch (message.Type)
        {
            case "init":
                _logger.LogInformation($"New connection initialization: {message.ConnectionId}");
                await HandleInitMessage(ws, message);
                break;

            case "data":
                await HandleDataMessage(ws, message);
                break;

            case "close":
                _logger.LogInformation($"Closing connection: {message.ConnectionId}");
                CloseConnection(message.ConnectionId);
                break;

            case "control":
                await HandleControlMessage(ws, message);
                break;
        }
    }

    private async Task HandleInitMessage(WebSocket ws, TunnelTcpMessage message)
    {
        try
        {
            var tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(_localHost, _localPort);
            _connections[message.ConnectionId] = tcpClient;
            _logger.LogInformation($"Local connection established for {message.ConnectionId}");

            // Démarrer la lecture des réponses TCP immédiatement
            _ = ProcessTcpResponses(ws, message.ConnectionId, tcpClient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to establish local connection for {message.ConnectionId}");
            // Informer le serveur de l'échec
            var errorMessage = new TunnelTcpMessage {
                Type = "error",
                ConnectionId = message.ConnectionId,
                Data = ex.Message
            };
            await SendWebSocketMessage(ws, errorMessage);
        }
    }

    private async Task HandleDataMessage(WebSocket ws, TunnelTcpMessage message)
    {
        if (string.IsNullOrEmpty(message.Data)) return;

        if (_connections.TryGetValue(message.ConnectionId, out var tcpClient))
        {
            try
            {
                var data = Convert.FromBase64String(message.Data);
                var stream = tcpClient.GetStream();
                await stream.WriteAsync(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error writing to local connection {message.ConnectionId}");
                CloseConnection(message.ConnectionId);
            }
        }
        else
        {
            _logger.LogWarning($"Received data for unknown connection: {message.ConnectionId}");
        }
    }

    private async Task ProcessTcpResponses(WebSocket ws, string connectionId, TcpClient tcpClient)
    {
        try
        {
            var buffer = new byte[8192]; // Augmenté à 8KB pour de meilleures performances
            var stream = tcpClient.GetStream();

            while (tcpClient.Connected && !_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;

                var message = new TunnelTcpMessage {
                    Type = "data",
                    ConnectionId = connectionId,
                    Data = Convert.ToBase64String(buffer, 0, bytesRead)
                };

                await SendWebSocketMessage(ws, message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error reading from local connection {connectionId}");
        }
        finally
        {
            CloseConnection(connectionId);
        }
    }

    private async Task HandleControlMessage(WebSocket ws, TunnelTcpMessage message)
    {
        if (message.ConnectionId == "close" && !string.IsNullOrEmpty(message.Data))
        {
            CloseConnection(message.Data);
        }
    }

    private void CloseConnection(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var client))
        {
            client.Close();
            _connections.Remove(connectionId);
        }
    }

    private async Task SendWebSocketMessage(WebSocket ws, TunnelTcpMessage message)
    {
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text, true, _cts.Token);
    }

    public void Stop()
    {
        _cts.Cancel();
        foreach (var client in _connections.Values)
        {
            client.Close();
        }
        _connections.Clear();
    }
}