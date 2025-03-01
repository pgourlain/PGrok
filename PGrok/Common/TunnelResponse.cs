using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGrok.Common.Models;

public class TunnelResponse
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("body")]
    public byte[]? Body { get; set; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    // WebSocket specific properties
    [JsonPropertyName("isWebSocket")]
    public bool IsWebSocket { get; set; } = false;

    [JsonPropertyName("webSocketAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebSocketAction { get; set; } // "accept", "upgrade", "message", "close", "error"

    [JsonPropertyName("webSocketData")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebSocketData { get; set; } // Base64 encoded message data

    [JsonPropertyName("webSocketMessageType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebSocketMessageType { get; set; } // "text" or "binary"

    [JsonPropertyName("webSocketFinal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? WebSocketFinal { get; set; } // Indicates if this is the final fragment of a message

    [JsonPropertyName("webSocketCloseReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebSocketCloseReason { get; set; }

    [JsonIgnore] // Don't serialize this property
    public WebSocket? WebSocket { get; set; } // Only used server-side, not serialized

    [JsonPropertyName("webSocketRequest")]
    public bool WebSocketRequest { get; set; } = false;

    // Meta properties for telemetry/debugging
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? Timestamp { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("processingTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TimeSpan ProcessingTime { get; set; }

    // Factory methods for creating common responses
    public static TunnelResponse FromException(Exception ex)
    {
        return new TunnelResponse {
            StatusCode = 500,
            ErrorMessage = ex.Message,
            Headers = new Dictionary<string, string> {
                ["Content-Type"] = "application/json"
            },
            Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new {
                    error = "Internal Server Error",
                    message = ex.Message,
                    timestamp = DateTime.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            ))
        };
    }

    public static TunnelResponse CreateSuccessResponse(string? requestId = null, byte[]? body = null)
    {
        return new TunnelResponse {
            RequestId = requestId,
            StatusCode = 200,
            Headers = new Dictionary<string, string> {
                ["Content-Type"] = "application/json"
            },
            Body = body ?? Encoding.UTF8.GetBytes("{\"success\":true}")
        };
    }

    public static TunnelResponse CreateErrorResponse(int statusCode, string error, string? requestId = null)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { error, timestamp = DateTime.UtcNow },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        ));

        return new TunnelResponse {
            RequestId = requestId,
            StatusCode = statusCode,
            ErrorMessage = error,
            Headers = new Dictionary<string, string> {
                ["Content-Type"] = "application/json"
            },
            Body = body
        };
    }

    public static TunnelResponse CreateWebSocketResponse(string requestId, string action)
    {
        return new TunnelResponse {
            RequestId = requestId,
            StatusCode = 200,
            IsWebSocket = true,
            WebSocketAction = action,
            WebSocketRequest = true
        };
    }

    // Helper methods
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    public bool IsError => StatusCode >= 400;

    public string GetContentType()
    {
        return Headers.TryGetValue("Content-Type", out var value) ? value : "";
    }

    public bool IsJson()
    {
        return GetContentType().Contains("application/json", StringComparison.OrdinalIgnoreCase);
    }

    public string GetBodyAsString()
    {
        if (Body == null || Body.Length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(Body);
    }

    public T? GetBodyAsJson<T>()
    {
        if (Body == null || Body.Length == 0)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Body);
        }
        catch
        {
            return default;
        }
    }
}
