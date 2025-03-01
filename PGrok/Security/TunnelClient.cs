

namespace PGrok.Security;


/// <summary>
/// Represents an authorized client for the tunnel service
/// </summary>
public class TunnelClient
{
    public string ApiKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxTunnels { get; set; } = 5;
    public int RateLimit { get; set; } = 100;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public List<string> ActiveTunnels { get; set; } = new();

    public bool HasReachedTunnelLimit => ActiveTunnels.Count >= MaxTunnels;
}