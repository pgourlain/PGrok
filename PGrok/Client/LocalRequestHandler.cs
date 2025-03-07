using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

// This class would be used in the ProcessTunnelMessages method of the local YARP server
public class LocalRequestHandler
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _localWebServerBaseUrl;

    public LocalRequestHandler(
        ILogger logger,
        string? localUrl)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _localWebServerBaseUrl = localUrl ?? "http://localhost:5001";
    }

    public async Task HandleRequestAsync(
        byte[] messageData,
        WebSocket clientWebSocket,
        CancellationToken cancellationToken)
    {
        try
        {
            // Deserialize the request from the WebSocket message
            var request = DeserializeRequest(messageData);

            // Forward to local web server
            var response = await ForwardToLocalServerAsync(request, cancellationToken);

            // Serialize and send the response back
            var responseData = await SerializeResponseAsync(response);
            await clientWebSocket.SendAsync(
                new ArraySegment<byte>(responseData),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling tunneled request");

            // Send error response
            var errorResponse = SerializeErrorResponse(ex);
            await clientWebSocket.SendAsync(
                new ArraySegment<byte>(errorResponse),
                WebSocketMessageType.Text,
                true,
                cancellationToken);
        }
    }

    private TunneledRequest DeserializeRequest(byte[] data)
    {
        // This would parse the JSON or binary format used for tunneling
        var json = Encoding.UTF8.GetString(data);
        return JsonSerializer.Deserialize<TunneledRequest>(json);
    }

    private async Task<HttpResponseMessage> ForwardToLocalServerAsync(
        TunneledRequest request,
        CancellationToken cancellationToken)
    {
        // Create a new HTTP request to the local server
        var url = $"{_localWebServerBaseUrl}{request.Path}";

        using var httpRequest = new HttpRequestMessage(
            new HttpMethod(request.Method), url);

        // Add headers
        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        // Add body if present
        if (request.Body != null)
        {
            httpRequest.Content = new ByteArrayContent(request.Body);
        }

        // Send to local server
        return await _httpClient.SendAsync(httpRequest, cancellationToken);
    }

    private async Task<byte[]> SerializeResponseAsync(HttpResponseMessage response)
    {
        // Read the response body
        var bodyBytes = await response.Content.ReadAsByteArrayAsync();

        // Create response object
        var tunnelResponse = new TunneledResponse {
            StatusCode = (int)response.StatusCode,
            Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            ContentHeaders = response.Content.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
            Body = bodyBytes
        };

        var json = JsonSerializer.Serialize(tunnelResponse);
        // Serialize to JSON
        return Encoding.UTF8.GetBytes(json);
    }

    private byte[] SerializeErrorResponse(Exception ex)
    {
        var tunnelResponse = new TunneledResponse {
            StatusCode = 500,
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "text/plain" }
            },
            Body = Encoding.UTF8.GetBytes($"Error processing request: {ex.Message}")
        };

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tunnelResponse));
    }
}

// Data transfer objects for the tunnel protocol
public class TunneledRequest
{
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("contentheaders")]
    public Dictionary<string, string>? ContentHeaders { get; set; }
    [JsonPropertyName("body")]
    public byte[]? Body { get; set; } 
}

public class TunneledResponse
{
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; set; }
    [JsonPropertyName("body")]
    public byte[]? Body { get; set; } // Base64 encoded
    [JsonPropertyName("contentheaders")]
    public Dictionary<string, string>? ContentHeaders { get; set; }
}