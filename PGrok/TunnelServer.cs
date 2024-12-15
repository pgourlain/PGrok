using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Server;

public class HttpTunnelServer
{
    private class TunnelConnection
    {
        public WebSocket WebSocket { get; set; }
        public TaskCompletionSource<string> ResponseWaiter { get; set; }
        public CancellationTokenSource CancellationToken { get; set; }
    }

    private readonly HttpListener _listener;
    private readonly ConcurrentDictionary<string, TunnelConnection> _tunnels;
    private readonly int _port;
    private readonly bool _uselocalhost;

    public HttpTunnelServer(int port = 80, bool uselocalhost = false)
    {
        _port = port;
        _uselocalhost = uselocalhost; //for debugging purposes
        _listener = new HttpListener();
        _tunnels = new ConcurrentDictionary<string, TunnelConnection>();
    }

    public async Task Start()
    {
        var host = _uselocalhost ? "localhost" : "+";
        _listener.Prefixes.Add($"http://{host}:{_port}/");
        _listener.Start();

        Console.WriteLine($"Server listening on port {_port}");
        Console.WriteLine("Ready to accept connections");

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
                context.Response.StatusCode = 400;
                var message = "WebSocket connection required";
                var buffer = Encoding.UTF8.GetBytes(message);
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();
            }
        }
        else if (!string.IsNullOrEmpty(tunnelId))
        {
            await HandleHttpRequest(context, tunnelId);
        }
        else
        {
            await SendHomePage(context);
        }
    }

    private async Task SendHomePage(HttpListenerContext context)
    {
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

        var buffer = Encoding.UTF8.GetBytes(html.ToString());
        context.Response.ContentType = "text/html";
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }

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

            var tunnelConnection = new TunnelConnection
            {
                WebSocket = wsContext.WebSocket,
                ResponseWaiter = new TaskCompletionSource<string>(),
                CancellationToken = new CancellationTokenSource()
            };

            if (_tunnels.TryAdd(tunnelId, tunnelConnection))
            {
                Console.WriteLine($"New tunnel registered: {tunnelId}");
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
            Console.WriteLine($"Error in tunnel connection: {ex.Message}");
        }
    }

    private async Task ProcessTunnelMessages(string tunnelId, TunnelConnection connection)
    {
        try
        {
            var buffer = new byte[4096];
            var ms = new MemoryStream();

            while (connection.WebSocket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;

                do
                {
                    result = await connection.WebSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        connection.CancellationToken.Token
                    );

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    await ms.WriteAsync(buffer.AsMemory(0, result.Count), connection.CancellationToken.Token);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    var response = Encoding.UTF8.GetString(ms.ToArray());
                    connection.ResponseWaiter.TrySetResult(response);
                    connection.ResponseWaiter = new TaskCompletionSource<string>();
                }
            }
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpListenerException inner)
        {
            if (inner.NativeErrorCode != 995) //The I/O operation has been aborted because of either a thread exit or an application request.
            {
                Console.WriteLine($"Error processing tunnel messages: {ex.Message}");
            }
            connection.ResponseWaiter.TrySetException(ex);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing tunnel messages: {ex.Message}");
            connection.ResponseWaiter.TrySetException(ex);
        }
        finally
        {
            if (_tunnels.TryRemove(tunnelId, out var _))
            {
                Console.WriteLine($"Tunnel closed: {tunnelId}");
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
                    Console.WriteLine($"Error closing WebSocket: {ex.Message}");
                }
            }
        }
    }

    private async Task HandleHttpRequest(HttpListenerContext context, string tunnelId)
    {
        if (!_tunnels.TryGetValue(tunnelId, out var tunnel))
        {
            context.Response.StatusCode = 404;
            var message = JsonSerializer.Serialize(new
            {
                error = "Tunnel Not Found",
                message = $"No tunnel found for service: {tunnelId}",
                availableTunnels = _tunnels.Keys.ToList()
            });
            context.Response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(message);
            await context.Response.OutputStream.WriteAsync(buffer);
            context.Response.Close();
            return;
        }

        try
        {
            var request = new TunnelRequest
            {
                Method = context.Request.HttpMethod,
                Url = context.Request.Url?.ToString() ?? string.Empty,
                Headers = HttpHelpers.GetHeaders(context.Request),
                Body = await HttpHelpers.GetRequestBodyAsync(context.Request)
            };

            var requestJson = JsonSerializer.Serialize(request);
            tunnel.ResponseWaiter = new TaskCompletionSource<string>();

            await WebSocketHelpers.SendStringAsync(tunnel.WebSocket, requestJson);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var responseJson = await tunnel.ResponseWaiter.Task.WaitAsync(cts.Token);
                var response = JsonSerializer.Deserialize<TunnelResponse>(responseJson);

                if (response != null)
                {
                    context.Response.StatusCode = response.StatusCode;
                    if (response.Headers != null)
                    {
                        foreach (var (key, value) in response.Headers)
                        {
                            context.Response.Headers.Add(key, value);
                        }
                    }

                    if (!string.IsNullOrEmpty(response.Body))
                    {
                        var buffer = Encoding.UTF8.GetBytes(response.Body);
                        await context.Response.OutputStream.WriteAsync(buffer);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                context.Response.StatusCode = 504;
                var message = JsonSerializer.Serialize(new
                {
                    error = "Gateway Timeout",
                    message = "The tunnel client did not respond in time",
                    tunnelId = tunnelId
                });
                context.Response.ContentType = "application/json";
                var buffer = Encoding.UTF8.GetBytes(message);
                await context.Response.OutputStream.WriteAsync(buffer);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling HTTP request: {ex.Message}");
            context.Response.StatusCode = 500;
            var message = JsonSerializer.Serialize(new
            {
                error = "Internal Server Error",
                message = "An error occurred while processing the request",
                details = ex.Message
            });
            context.Response.ContentType = "application/json";
            var buffer = Encoding.UTF8.GetBytes(message);
            await context.Response.OutputStream.WriteAsync(buffer);
        }
        finally
        {
            context.Response.Close();
        }
    }
}