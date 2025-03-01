using System.Text.Json.Serialization;

namespace PGrok.Common.Models;

/// <summary>
/// Represents a TCP tunnel message for forwarding TCP connections
/// </summary>
public class TunnelTcpMessage
{
    /// <summary>
    /// The type of message: "connect", "data", "close", or "error"
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The unique identifier for the TCP connection
    /// </summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Base64 encoded data for the TCP message
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>
    /// For connect messages, the host to connect to
    /// </summary>
    [JsonPropertyName("host")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    /// <summary>
    /// For connect messages, the port to connect to
    /// </summary>
    [JsonPropertyName("port")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Port { get; set; }

    /// <summary>
    /// For error messages, the error description
    /// </summary>
    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>
    /// Timestamp of the message
    /// </summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Helper methods for creating specific message types

    /// <summary>
    /// Creates a connection request message
    /// </summary>
    public static TunnelTcpMessage CreateConnectMessage(string connectionId, string host, int port)
    {
        return new TunnelTcpMessage {
            Type = "connect",
            ConnectionId = connectionId,
            Host = host,
            Port = port
        };
    }

    /// <summary>
    /// Creates a data message with the provided binary data
    /// </summary>
    public static TunnelTcpMessage CreateDataMessage(string connectionId, byte[] data)
    {
        return new TunnelTcpMessage {
            Type = "data",
            ConnectionId = connectionId,
            Data = Convert.ToBase64String(data)
        };
    }

    /// <summary>
    /// Creates a close message for the specified connection
    /// </summary>
    public static TunnelTcpMessage CreateCloseMessage(string connectionId)
    {
        return new TunnelTcpMessage {
            Type = "close",
            ConnectionId = connectionId
        };
    }

    /// <summary>
    /// Creates an error message
    /// </summary>
    public static TunnelTcpMessage CreateErrorMessage(string connectionId, string error)
    {
        return new TunnelTcpMessage {
            Type = "error",
            ConnectionId = connectionId,
            Error = error
        };
    }

    /// <summary>
    /// Decodes the data field from Base64 to a byte array
    /// </summary>
    public byte[]? GetDecodedData()
    {
        if (string.IsNullOrEmpty(Data))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(Data);
        }
        catch
        {
            return null;
        }
    }
}