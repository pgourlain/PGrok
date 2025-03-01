using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Security;
/// <summary>
/// Extensions for HTTP authentication and security
/// </summary>
public static class HttpSecurityExtensions
{
    /// <summary>
    /// Checks if a request is authenticated
    /// </summary>
    public static bool IsAuthenticated(this HttpListenerContext context, TunnelAuthenticationService authService)
    {
        bool isAuthenticated = authService.AuthenticateClient(context.Request, out var client);

        if (!isAuthenticated)
        {
            context.Response.StatusCode = 401;
            context.Response.Headers.Add("WWW-Authenticate", "PGrok-Key");
            context.Response.Close();
        }

        return isAuthenticated;
    }

    /// <summary>
    /// Checks if a request is within rate limits
    /// </summary>
    public static bool IsWithinRateLimit(this HttpListenerContext context, TunnelAuthenticationService authService, string clientId, string endpoint)
    {
        bool isWithinLimit = authService.CheckRateLimit(clientId, endpoint);

        if (!isWithinLimit)
        {
            context.Response.StatusCode = 429; // Too Many Requests
            context.Response.Headers.Add("Retry-After", "60");
            context.Response.Close();
        }

        return isWithinLimit;
    }

    /// <summary>
    /// Adds CORS headers to a response
    /// </summary>
    public static void AddCorsHeaders(this HttpListenerResponse response, string origins = "*")
    {
        response.Headers.Add("Access-Control-Allow-Origin", origins);
        response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
        response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, X-PGrok-API-Key");
        response.Headers.Add("Access-Control-Max-Age", "86400");
    }

    /// <summary>
    /// Adds security headers to a response
    /// </summary>
    public static void AddSecurityHeaders(this HttpListenerResponse response)
    {
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add("X-XSS-Protection", "1; mode=block");
        response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
        response.Headers.Add("Content-Security-Policy", "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';");
    }
}