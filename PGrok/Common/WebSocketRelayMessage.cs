using System.Net.WebSockets;
using System.Text.Json.Serialization;

namespace PGrok.Common.Models;

/// <summary>
/// Class for WebSocket relay messages between the tunnel client and server
/// </summary>
public class WebSocketRelayMessage
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    [JsonPropertyName("messageType")]
    public WebSocketMessageType MessageType { get; set; }

    [JsonPropertyName("endOfMessage")]
    public bool EndOfMessage { get; set; }

    [JsonPropertyName("data")]
    public byte[]? Data { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Helper properties and methods
    [JsonIgnore]
    public bool IsText => MessageType == WebSocketMessageType.Text;

    [JsonIgnore]
    public bool IsBinary => MessageType == WebSocketMessageType.Binary;

    [JsonIgnore]
    public bool IsClose => MessageType == WebSocketMessageType.Close;

    [JsonIgnore]
    public int DataLength => Data?.Length ?? 0;

    /// <summary>
    /// Creates a relay message for a text message
    /// </summary>
    public static WebSocketRelayMessage CreateTextMessage(string connectionId, string text, bool endOfMessage = true)
    {
        return new WebSocketRelayMessage {
            ConnectionId = connectionId,
            MessageType = WebSocketMessageType.Text,
            EndOfMessage = endOfMessage,
            Data = System.Text.Encoding.UTF8.GetBytes(text)
        };
    }

    /// <summary>
    /// Creates a relay message for a binary message
    /// </summary>
    public static WebSocketRelayMessage CreateBinaryMessage(string connectionId, byte[] data, bool endOfMessage = true)
    {
        return new WebSocketRelayMessage {
            ConnectionId = connectionId,
            MessageType = WebSocketMessageType.Binary,
            EndOfMessage = endOfMessage,
            Data = data
        };
    }

    /// <summary>
    /// Creates a close message
    /// </summary>
    public static WebSocketRelayMessage CreateCloseMessage(string connectionId)
    {
        return new WebSocketRelayMessage {
            ConnectionId = connectionId,
            MessageType = WebSocketMessageType.Close,
            EndOfMessage = true
        };
    }
}
