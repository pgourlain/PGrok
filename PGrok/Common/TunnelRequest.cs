using System.Text.Json.Serialization;

namespace PGrok.Common.Models;

public class TunnelRequest
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("body")]
    public byte[]? Body { get; set; }

    [JsonPropertyName("isWebSocketRequest")]
    public bool IsWebSocketRequest { get; set; } = false;

    [JsonPropertyName("isBlazorRequest")]
    public bool IsBlazorRequest { get; set; } = false;

}
