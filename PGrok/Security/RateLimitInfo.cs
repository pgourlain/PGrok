using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Security;
/// <summary>
/// Tracks rate limiting information
/// </summary>
internal class RateLimitInfo
{
    public int Count { get; set; }
    public DateTime ResetTime { get; set; }
}


/// <summary>
/// Configuration for the authentication service
/// </summary>
public class TunnelAuthConfig
{
    public bool EnableAuthentication { get; set; } = false;
    public bool EnableRateLimiting { get; set; } = false;
    public int DefaultRateLimit { get; set; } = 100;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public List<ApiKeyConfig>? AuthorizedKeys { get; set; }
}


/// <summary>
/// Configuration for an API key
/// </summary>
public class ApiKeyConfig
{
    public string ApiKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxTunnels { get; set; } = 5;
    public int RateLimit { get; set; } = 100;
}


