
// PGrok.Client/HttpTunnelClient.cs
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PGrok.Common.Helpers;
using PGrok.Common.Models;

namespace PGrok.Client;

public class HttpTunnelClient
{
    private readonly string _serverUrl;
    private readonly string _tunnelId;
    private readonly ILogger _logger;
    private readonly string _localUrl;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cts;
    private readonly int? _proxyPort;
    private readonly HttpListener _listener;
    private readonly Dictionary<string, HttpListenerContext> _currentRequests = new Dictionary<string, HttpListenerContext>();
    private ClientWebSocket? _currentWS = null;

    public HttpTunnelClient(string serverUrl, string tunnelId, string localUrl, int? proxyPort, ILogger logger)
    {
        _serverUrl = serverUrl;
        _tunnelId = tunnelId;
        _logger = logger;
        _localUrl = localUrl.TrimEnd('/');
        _httpClient = new HttpClient();
        _proxyPort = proxyPort;
        _listener = new HttpListener();
        _cts = new CancellationTokenSource();
    }

    public async Task Start()
    {
        _logger.LogInformation($"Starting tunnel client for service {_tunnelId}");
        _logger.LogInformation($"Tunnel Server: {_serverUrl}");
        _logger.LogInformation($"Forward http calls to: {_localUrl}");
        if (_proxyPort.HasValue)
        {
            _logger.LogInformation($"Reverse proxy mode is activated on http://localhost:{_proxyPort}");
            _logger.LogInformation($"- http://localhost:{_proxyPort}/[service]/.... make an http call on tunnel server side on specified service. This call will be translate like this http://service:[sererReverseProxyPort]/...");
            _listener.Prefixes.Add($"http://localhost:{_proxyPort}/");
            _listener.Start();
        }

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

        await ProcessMessages(ws);
    }

    private async Task ProcessMessages(ClientWebSocket ws)
    {
        using var httpCts = new CancellationTokenSource();
        using var ms = new MemoryStream();
        _currentWS = ws;
        var httpTask = Task.Factory.StartNew(() => ProcessHttpProxyCall(), httpCts.Token);
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            {
                var message = await WebSocketHelpers.ReceiveStringAsync(ws, ms, _cts.Token);
                if (ws.State == WebSocketState.CloseReceived && ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token);
                    //single mode
                    throw new Exception(ws.CloseStatusDescription);
                }
                //check if message is a valid request or a response of proxy request
                if (message.StartsWith("$dispatchresponse$"))
                {
                    var dispatchResponse = JsonSerializer.Deserialize<TunnelResponse>(message.AsSpan(18));
                    if (dispatchResponse == null)
                    {
                        _logger.LogWarning("Received invalid dispatch response");
                        continue;
                    }

                    if (dispatchResponse?.Headers?.TryGetValue(HttpHelpers.RequestIdHeader, out var requestId) == true)
                    {
                        if (_currentRequests.TryGetValue(requestId, out var context))
                        {
                            _currentRequests.Remove(requestId);
                            await context.Response.HandleResponse(dispatchResponse);
                            continue;
                        }
                        else
                        {
                            _logger.LogWarning("Received dispatch response for unknown request");
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Received dispatch response without X-PGrok-RequestId");
                        continue;
                    }
                }

                var request = JsonSerializer.Deserialize<TunnelRequest>(message);

                if (request == null)
                {
                    _logger.LogWarning("Received invalid request");
                    continue;
                }

                try
                {
                    var response = await HttpHelpers.ForwardRequestToLocalService(request, _tunnelId, _localUrl, _httpClient, _logger);
                    var responseJson = JsonSerializer.Serialize(response);
                    await WebSocketHelpers.SendStringAsync(ws, responseJson, _cts.Token);
                }
                catch (Exception ex)
                {
                    var errorResponse = TunnelResponse.FromException(ex);
                    var errorJson = JsonSerializer.Serialize(errorResponse);
                    await WebSocketHelpers.SendStringAsync(ws, errorJson, _cts.Token);
                    _logger.LogError($"Error processing request: {ex.Message}");
                }
            }
        }
        finally
        {
            // Cancel the http proxy task
            httpCts.Cancel();
        }
    }

    private async Task ProcessHttpProxyCall()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync();
            if (_currentWS?.State != WebSocketState.Open)
            {
                await context.Response.HandleXXX($"Tunnel Service Unavailable", 503, true);
                continue;
            }
            _ = HandleProxyContextAsync(context, _currentWS);
        }
    }

    private async Task HandleProxyContextAsync(HttpListenerContext context, ClientWebSocket ws)
    {
        var path = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
        var segments = path.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        var serviceId = segments[0];
        if (string.IsNullOrEmpty(serviceId))
        {
            await context.Response.Handle400("Service to proxy is required. ");
            return;
        }
        _logger.LogInformation($"Call to proxied: {serviceId}{(segments.Length > 1 ? ("/" + segments[1]) : "")}");

        var request = new TunnelRequest {
            RequestId = Guid.NewGuid().ToString(),
            Method = context.Request.HttpMethod,
            Url = context.Request.Url?.ToString() ?? string.Empty,
            Headers = HttpHelpers.GetHeaders(context.Request),
            Body = await HttpHelpers.GetRequestBodyAsync(context.Request)
        };
        _currentRequests.Add(request.RequestId!, context);
        var requestJson = "$dispatch$" + JsonSerializer.Serialize(request);
        await WebSocketHelpers.SendStringAsync(ws, requestJson);
    }

    public void Stop()
    {
        _cts.Cancel();
    }
}
