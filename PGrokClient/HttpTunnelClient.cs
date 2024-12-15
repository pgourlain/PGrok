
// PGrok.Client/HttpTunnelClient.cs
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

    public HttpTunnelClient(string serverUrl, string tunnelId, string localUrl, ILogger logger)
    {
        _serverUrl = serverUrl;
        _tunnelId = tunnelId;
        _logger = logger;
        _localUrl = localUrl.TrimEnd('/');
        _httpClient = new HttpClient();
        _cts = new CancellationTokenSource();
    }

    public async Task Start()
    {
        _logger.LogInformation($"Starting tunnel client for service {_tunnelId}");
        _logger.LogInformation($"Server: {_serverUrl}");
        _logger.LogInformation($"Local service: {_localUrl}");

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
        while (ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
        {
            var message = await WebSocketHelpers.ReceiveStringAsync(ws, _cts.Token);
            if (ws.State == WebSocketState.CloseReceived && ws.CloseStatus == WebSocketCloseStatus.PolicyViolation)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token);
                //single mode
                throw new Exception(ws.CloseStatusDescription);
            }
            var request = JsonSerializer.Deserialize<TunnelRequest>(message);

            if (request == null)
            {
                _logger.LogWarning("Received invalid request");
                continue;
            }

            try
            {
                var response = await ForwardRequestToLocalService(request);
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

    private async Task<TunnelResponse> ForwardRequestToLocalService(TunnelRequest request)
    {
        try
        {
            var uri = new Uri(request.Url!);
            var segments = uri.AbsolutePath.Split(new[] { $"/{_tunnelId}/" }, 2, StringSplitOptions.None);
            var localPath = segments.Length > 1 ? segments[1] : segments[0].TrimStart('/');

            var localUrl = $"{_localUrl}/{localPath}{uri.Query}";

            using var httpRequest = new HttpRequestMessage
            {
                Method = new HttpMethod(request.Method!),
                RequestUri = new Uri(localUrl)
            };

            // Copy headers
            if (request.Headers != null)
            {
                foreach (var (key, value) in request.Headers)
                {
                    if (!key.StartsWith(":") && key.ToLower() is not ("host" or "connection"))
                    {
                        httpRequest.Headers.TryAddWithoutValidation(key, value);
                    }
                }
            }

            // Add body if present
            if (!string.IsNullOrEmpty(request.Body))
            {
                httpRequest.Content = new StringContent(request.Body);
                if (request.Headers!=null && request.Headers.TryGetValue("Content-Type", out var contentType))
                {
                    httpRequest.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                }
            }

            _logger.LogInformation($"Forwarding request: {request.Method} {localUrl}");

            var response = await _httpClient.SendAsync(httpRequest);

            return new TunnelResponse
            {
                StatusCode = (int)response.StatusCode,
                Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                Body = await response.Content.ReadAsStringAsync()
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError($"HTTP Request error: {ex.Message}");
            return CreateErrorResponse(503, "Service Unavailable", "The local service is not responding", ex.Message);
        }
        catch (UriFormatException ex)
        {
            _logger.LogError($"Invalid URL format: {ex.Message}");
            return CreateErrorResponse(400, "Bad Request", "Invalid URL format", ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Unexpected error: {ex.Message}");
            return CreateErrorResponse(500, "Internal Server Error", "An unexpected error occurred", ex.Message);
        }
    }

    private static TunnelResponse CreateErrorResponse(int statusCode, string error, string message, string details)
    {
        return new TunnelResponse
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
            Body = JsonSerializer.Serialize(new { error, message, details })
        };
    }

    public void Stop()
    {
        _cts.Cancel();
    }
}
