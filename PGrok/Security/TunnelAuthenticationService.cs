using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PGrok.Security;

/// <summary>
/// Provides authentication and authorization services for PGrok tunnel connections
/// </summary>
public class TunnelAuthenticationService
{
    private readonly ILogger _logger;
    private readonly TunnelAuthConfig _config;
    private readonly Dictionary<string, TunnelClient> _authorizedClients = new();
    private readonly Dictionary<string, RateLimitInfo> _rateLimits = new();

    public TunnelAuthenticationService(ILogger logger, TunnelAuthConfig config)
    {
        _logger = logger;
        _config = config;

        // Load initial clients from config
        if (config.AuthorizedKeys?.Any() == true)
        {
            foreach (var key in config.AuthorizedKeys)
            {
                _authorizedClients[key.ApiKey] = new TunnelClient {
                    ApiKey = key.ApiKey,
                    Name = key.Name,
                    MaxTunnels = key.MaxTunnels,
                    RateLimit = key.RateLimit
                };
            }
        }
    }

    /// <summary>
    /// Authenticates a client based on the API key in the request
    /// </summary>
    public bool AuthenticateClient(HttpListenerRequest request, out TunnelClient? client)
    {
        client = null;

        if (!_config.EnableAuthentication)
        {
            return true; // Authentication disabled
        }

        // Check for API key in headers
        string? apiKey = request.Headers["X-PGrok-API-Key"];

        // Check for API key in query string if not in headers
        if (string.IsNullOrEmpty(apiKey) && request.QueryString["api_key"] != null)
        {
            apiKey = request.QueryString["api_key"];
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Authentication failed: No API key provided");
            return false;
        }

        // Check if the API key is valid
        if (!_authorizedClients.TryGetValue(apiKey, out client))
        {
            _logger.LogWarning("Authentication failed: Invalid API key");
            return false;
        }

        // Update last activity
        client.LastActivity = DateTime.UtcNow;

        return true;
    }

    /// <summary>
    /// Checks if the rate limit for a client has been exceeded
    /// </summary>
    public bool CheckRateLimit(string clientId, string endpoint)
    {
        if (!_config.EnableRateLimiting)
        {
            return true; // Rate limiting disabled
        }

        string key = $"{clientId}:{endpoint}";

        lock (_rateLimits)
        {
            // Clean up expired rate limits
            var now = DateTime.UtcNow;
            var expiredKeys = _rateLimits.Where(kv => kv.Value.ResetTime < now).Select(kv => kv.Key).ToList();
            foreach (var expiredKey in expiredKeys)
            {
                _rateLimits.Remove(expiredKey);
            }

            // Check/initialize rate limit for this client and endpoint
            if (!_rateLimits.TryGetValue(key, out var rateLimit))
            {
                rateLimit = new RateLimitInfo {
                    Count = 0,
                    ResetTime = now.AddSeconds(_config.RateLimitWindowSeconds)
                };
                _rateLimits[key] = rateLimit;
            }

            // Check if the rate limit has been exceeded
            if (rateLimit.Count >= _config.DefaultRateLimit)
            {
                _logger.LogWarning($"Rate limit exceeded for {clientId} on {endpoint}");
                return false;
            }

            // Increment the counter
            rateLimit.Count++;
            return true;
        }
    }

    /// <summary>
    /// Adds a new authorized client
    /// </summary>
    public TunnelClient AddClient(string name, int maxTunnels = 5, int rateLimit = 100)
    {
        string apiKey = GenerateApiKey();

        var client = new TunnelClient {
            ApiKey = apiKey,
            Name = name,
            MaxTunnels = maxTunnels,
            RateLimit = rateLimit,
            CreatedAt = DateTime.UtcNow,
            LastActivity = DateTime.UtcNow
        };

        _authorizedClients[apiKey] = client;
        _logger.LogInformation($"Added new client: {name} with API key: {apiKey}");

        return client;
    }

    /// <summary>
    /// Revokes an API key
    /// </summary>
    public bool RevokeApiKey(string apiKey)
    {
        if (_authorizedClients.Remove(apiKey))
        {
            _logger.LogInformation($"Revoked API key: {apiKey}");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Generates a new random API key
    /// </summary>
    private static string GenerateApiKey()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "").Replace("/", "").Replace("=", "");
    }
}