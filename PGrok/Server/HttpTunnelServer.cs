using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Server;

public class HttpTunnelServer
{
    private class TunnelConnection
    {
        public required WebSocket WebSocket { get; set; }
        public required TaskCompletionSource<string> ResponseWaiter { get; set; }
        public required CancellationTokenSource CancellationToken { get; set; }
    }

    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, TunnelConnection> _tunnels;
    private readonly ILogger _logger;
    private readonly int _port;
    private readonly bool _uselocalhost;
    private readonly bool _useSingleTunnel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly int _proxyPort;

    public HttpTunnelServer(ILogger logger, IHttpClientFactory httpClientFactory, int port = 80, bool uselocalhost = false, bool useSingleTunnel = false, int proxyPort = 8080)
    {
        this._logger = logger;
        _port = port;
        _uselocalhost = uselocalhost; //for debugging purposes
        _useSingleTunnel = useSingleTunnel;
        _listener = new HttpListener();
        //each pgrok-client connection will be stored in this dictionary
        _tunnels = new ConcurrentDictionary<string, TunnelConnection>();
        _httpClientFactory = httpClientFactory;
        _proxyPort = proxyPort;
    }

    public async Task Start()
    {
        var host = _uselocalhost ? "localhost" : "+";
        _listener.Prefixes.Add($"http://{host}:{_port}/");
        _listener.Start();

        _logger.LogInformation($"Server listening on port {_port}");
        if (_useSingleTunnel)
        {
            _logger.LogInformation("Single tunnel mode enabled");
        }
        _logger.LogInformation($"Server dispatch http calls from clients on port {_proxyPort}");
        _logger.LogInformation("Ready to accept connections");

        while (true)
        {
            var context = await _listener.GetContextAsync();
            _ = HandleContextAsync(context);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
        var segments = path.Split('/', 2);
        var tunnelId = segments[0];

        if (path == "tunnel")
        {
            if (context.Request.IsWebSocketRequest)
            {
                await HandleTunnelConnection(context);
            }
            else
            {
                await context.Response.Handle400("WebSocket connection required");
            }
        }
        else if (path == "$status")
        {
            await SendHomePage(context);
        }
        else if ((!_useSingleTunnel && !string.IsNullOrEmpty(tunnelId)) || _useSingleTunnel)
        {
            await HandleHttpRequest(context, _useSingleTunnel ? string.Empty : tunnelId);
        }
        else
        {
            await SendHomePage(context);
        }
    }

    private async Task SendHomePage(HttpListenerContext context)
    {
        var mode = string.Empty;
        if (_useSingleTunnel)
        {
            mode = "Single tunnel mode is activated.";
        }
        var html = new StringBuilder()
            .AppendLine("<!DOCTYPE html>")
            .AppendLine("<html>")
            .AppendLine("<head>")
            .AppendLine("    <title>PGrok - Active Tunnels</title>")
            .AppendLine("    <style>")
            .AppendLine("        body { font-family: Arial, sans-serif; margin: 40px; }")
            .AppendLine("        h1 { color: #333; }")
            .AppendLine("        .tunnel-list { list-style-type: none; padding: 0; }")
            .AppendLine("        .tunnel-item { padding: 10px; margin: 5px 0; background: #f5f5f5; border-radius: 4px; }")
            .AppendLine("        .no-tunnels { color: #666; font-style: italic; }")
            .AppendLine("    </style>")
            .AppendLine("</head>")
            .AppendLine("<body>")
            .AppendLine($"    <b>{mode}</b>")
            .AppendLine("    <h1>PGrok Active Tunnels</h1>");

        if (_tunnels.IsEmpty)
        {
            html.AppendLine("    <p class=\"no-tunnels\">No active tunnels</p>");
        }
        else
        {
            html.AppendLine("    <ul class=\"tunnel-list\">");
            foreach (var tunnel in _tunnels)
            {
                html.AppendLine($"        <li class=\"tunnel-item\">{tunnel.Key}</li>");
            }
            html.AppendLine("    </ul>");
        }

        html.AppendLine("</body>")
            .AppendLine("</html>");

        context.Response.ContentType = "text/html";
        await context.Response.HandleXXX(html.ToString(), 200, true);    
    }

    /// <summary>
    /// handle all websocket connections from pgrokclient
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    private async Task HandleTunnelConnection(HttpListenerContext context)
    {
        try
        {
            var wsContext = await context.AcceptWebSocketAsync(null);
            var tunnelId = context.Request.QueryString["id"];

            if (string.IsNullOrEmpty(tunnelId))
            {
                await wsContext.WebSocket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Tunnel ID required",
                    CancellationToken.None
                );
                return;
            }

            if (_useSingleTunnel && !_tunnels.IsEmpty)
            {
                await wsContext.WebSocket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Single tunnel mode enabled and there one active tunnel",
                    CancellationToken.None
                );
                return;
            }

            var tunnelConnection = new TunnelConnection {
                WebSocket = wsContext.WebSocket,
                ResponseWaiter = new TaskCompletionSource<string>(),
                CancellationToken = new CancellationTokenSource()
            };

            if (_tunnels.TryAdd(tunnelId, tunnelConnection))
            {
                _logger.LogInformation($"New tunnel registered: {tunnelId}");
                await ProcessTunnelMessages(tunnelId, tunnelConnection);
            }
            else
            {
                await wsContext.WebSocket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Tunnel ID already in use",
                    CancellationToken.None
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in tunnel connection: {ex.Message}");
        }
    }

    private async Task ProcessTunnelMessages(string tunnelId, TunnelConnection connection)
    {
        try
        {
            using var ms = new MemoryStream();

            while (connection.WebSocket.State == WebSocketState.Open)
            {
                var response = await WebSocketHelpers.ReceiveStringAsync(connection.WebSocket, ms, connection.CancellationToken.Token);

                if (response?.StartsWith("$dispatch$") == true)
                {
                    //Call api on local server content is request

                    var request = JsonSerializer.Deserialize<TunnelRequest>(response.AsSpan(10));
                    if (request == null || string.IsNullOrWhiteSpace(request.Url))
                    {
                        _logger.LogWarning("Received invalid dispatch request");
                        continue;
                    }
                    var path = new Uri(request.Url).AbsolutePath.TrimStart('/') ?? string.Empty;
                    var segments = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
                    var serviceId = segments[0];

                    var httpResponse = await HttpHelpers.ForwardRequestToLocalService(request, serviceId, $"http://{serviceId}:{_proxyPort}", _httpClientFactory.CreateClient(), _logger, "pgrok-server:");
                    if (request.RequestId != null)
                    {
                        httpResponse.Headers ??= new Dictionary<string, string>();
                        httpResponse.Headers.Add(HttpHelpers.RequestIdHeader, request.RequestId ?? string.Empty);
                    }
                    var httpResponseData = "$dispatchresponse$" + JsonSerializer.Serialize(httpResponse);
                    await WebSocketHelpers.SendStringAsync(connection.WebSocket, httpResponseData);
                }
                else
                {
                    connection.ResponseWaiter.TrySetResult(response!);
                    connection.ResponseWaiter = new TaskCompletionSource<string>();
                }
            }
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpListenerException inner)
        {
            if (inner.NativeErrorCode != 995) //The I/O operation has been aborted because of either a thread exit or an application request.
            {
                _logger.LogError($"Error processing tunnel messages: {ex.Message}");
            }
            connection.ResponseWaiter.TrySetException(ex);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error processing tunnel messages: {ex.Message}");
            connection.ResponseWaiter.TrySetException(ex);
        }
        finally
        {
            if (_tunnels.TryRemove(tunnelId, out var _))
            {
                _logger.LogInformation($"Tunnel closed: {tunnelId}");
            }
            connection.CancellationToken.Cancel();

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
                    _logger.LogError($"Error closing WebSocket: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// handle all call from browsers or clients
    /// </summary>
    /// <param name="context"></param>
    /// <param name="tunnelId"></param>
    private async Task HandleHttpRequest(HttpListenerContext context, string tunnelId)
    {
        TunnelConnection? tunnel = null;

        if (!_useSingleTunnel)
        {
            _tunnels.TryGetValue(tunnelId, out tunnel);
        }
        else if (_tunnels.Count > 0)
        {
            tunnel = _tunnels.FirstOrDefault().Value;
        }
        if (tunnel == null)
        {
            var message = JsonSerializer.Serialize(new {
                error = "Tunnel Not Found",
                message = $"No tunnel found for service: {tunnelId}",
                availableTunnels = _tunnels.Keys.ToList()
            });
            context.Response.ContentType = "application/json";
            await context.Response.HandleXXX(message, 400, true);            
            return;
        }

        try
        {
            var request = new TunnelRequest {
                Method = context.Request.HttpMethod,
                Url = context.Request.Url?.ToString() ?? string.Empty,
                Headers = HttpHelpers.GetHeaders(context.Request),
                Body = await HttpHelpers.GetRequestBodyAsync(context.Request)
            };

            var requestJson = JsonSerializer.Serialize(request);
            tunnel.ResponseWaiter = new TaskCompletionSource<string>();

            await WebSocketHelpers.SendStringAsync(tunnel.WebSocket, requestJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                var responseJson = await tunnel.ResponseWaiter.Task.WaitAsync(cts.Token);
                var response = JsonSerializer.Deserialize<TunnelResponse>(responseJson);

                await context.Response.HandleResponse(response, false);
            }
            catch (OperationCanceledException)
            {
                var message = JsonSerializer.Serialize(new {
                    error = "Gateway Timeout",
                    message = "The tunnel client did not respond in time",
                    tunnelId = tunnelId
                });
                context.Response.ContentType = "application/json";
                await context.Response.HandleXXX(message, 504);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error handling HTTP request: {ex.Message}");

            var message = JsonSerializer.Serialize(new {
                error = "Internal Server Error",
                message = "An error occurred while processing the request",
                details = ex.Message
            });
            context.Response.ContentType = "application/json";
            await context.Response.HandleXXX(message, 500);
        }
        finally
        {
            context.Response.Close();
        }
    }
}