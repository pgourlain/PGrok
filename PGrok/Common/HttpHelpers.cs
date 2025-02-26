using Microsoft.Extensions.Logging;
using PGrok.Common.Models;
using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BinaryReader = System.IO.BinaryReader;

namespace PGrok.Common.Helpers;

public static class HttpHelpers
{
    private static readonly ArrayPool<byte> s_bytePool = ArrayPool<byte>.Shared;

    /// <summary>
    /// Gets the request body from an HttpListenerRequest.
    /// Uses buffer pooling for better memory efficiency.
    /// </summary>
    public static async Task<byte[]?> GetRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        // Check if we have a Content-Length header for optimization
        long? contentLength = null;
        if (long.TryParse(request.Headers["Content-Length"], out var length))
        {
            contentLength = length;
        }

        using var ms = new MemoryStream(contentLength.HasValue && contentLength.Value < int.MaxValue
            ? (int)contentLength.Value
            : 4096);

        byte[] buffer = s_bytePool.Rent(8192);

        try
        {
            int bytesRead;
            while ((bytesRead = await request.InputStream.ReadAsync(buffer)) > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, bytesRead));
            }

            return ms.ToArray();
        }
        finally
        {
            s_bytePool.Return(buffer);
        }
    }

    /// <summary>
    /// Gets headers from an HttpListenerRequest as a dictionary.
    /// </summary>
    public static Dictionary<string, string> GetHeaders(HttpListenerRequest request)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in request.Headers.AllKeys)
        {
            if (key != null && !string.IsNullOrEmpty(request.Headers[key]))
            {
                result[key] = request.Headers[key]!;
            }
        }

        return result;
    }

    /// <summary>
    /// Handles a 400 Bad Request response.
    /// </summary>
    public static async Task Handle400(this HttpListenerResponse response, string message)
    {
        await HandleXXX(response, message, 400, true);
    }

    /// <summary>
    /// Handles a 200 OK response.
    /// </summary>
    public static Task Handle200(this HttpListenerResponse response, string message)
    {
        return HandleXXX(response, message, 200);
    }

    /// <summary>
    /// Handles an HTTP response with the specified status code and message.
    /// </summary>
    public static async Task HandleXXX(this HttpListenerResponse response, string? message, int statusCode, bool closeStream = false)
    {
        try
        {
            response.StatusCode = statusCode;
            response.StatusDescription = GetStatusDescription(statusCode);

            if (message != null)
            {
                byte[] buffer = Encoding.UTF8.GetBytes(message);
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer);
            }

            if (closeStream)
            {
                response.Close();
            }
        }
        catch (HttpListenerException)
        {
            // Client disconnected
        }
        catch (ObjectDisposedException)
        {
            // Response was already closed
        }
    }

    /// <summary>
    /// Handles an HTTP response with a JSON object.
    /// </summary>
    public static async Task HandleXXX(this HttpListenerContext context, object messageContent, int statusCode, bool closeStream = false)
    {
        var message = JsonSerializer.Serialize(messageContent, new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        context.Response.ContentType = "application/json";
        await context.Response.HandleXXX(message, statusCode, closeStream);
    }

    /// <summary>
    /// Handles a tunnel response by setting appropriate headers and writing the body.
    /// </summary>
    public static async Task HandleResponse(this HttpListenerResponse response, TunnelResponse? tunnelResponse,
        bool isBlazorRequest, bool closeStream = true)
    {
        if (tunnelResponse == null)
        {
            response.StatusCode = 500;
            return;
        }

        try
        {
            response.StatusCode = tunnelResponse.StatusCode;
            response.StatusDescription = GetStatusDescription(tunnelResponse.StatusCode);

            // Set headers
            if (tunnelResponse.Headers != null)
            {
                foreach (var (key, value) in tunnelResponse.Headers)
                {
                    // Skip headers that HttpListener handles
                    if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "Connection", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        response.Headers[key] = value;
                    }
                    catch
                    {
                        // Some headers may not be settable, ignore
                    }
                }
            }

            // For Blazor, ensure caching headers are preserved
            if (isBlazorRequest)
            {
                // Ensure Blazor resources are properly cached
                if (!response.Headers.AllKeys.Contains("Cache-Control"))
                {
                    response.Headers.Add("Cache-Control", "public, max-age=3600");
                }
            }

            // Write body if present
            if (tunnelResponse.Body != null && tunnelResponse.Body.Length > 0)
            {
                response.ContentLength64 = tunnelResponse.Body.Length;
                await response.OutputStream.WriteAsync(tunnelResponse.Body);
            }

            // Close if requested
            if (closeStream)
            {
                response.Close();
            }
        }
        catch (HttpListenerException)
        {
            // Client disconnected
        }
        catch (ObjectDisposedException)
        {
            // Response was already closed
        }
    }

    /// <summary>
    /// Forwards a request to a local service and returns the response.
    /// </summary>
    public static async Task<TunnelResponse> ForwardRequestToLocalService(
        TunnelRequest request,
        string tunnelId,
        string localBaseUrl,
        HttpClient httpClient,
        ILogger logger,
        string caller = "")
    {
        try
        {
            // Parse the request URL
            var uri = new Uri(request.Url!);

            // Extract the path for forwarding
            var segments = uri.AbsolutePath.Split(new[] { $"/{tunnelId}/" }, 2, StringSplitOptions.None);
            var localPath = segments.Length > 1 ? segments[1] : segments[0].TrimStart('/');

            // Build the full local URL
            var fullLocalUrl = $"{localBaseUrl}/{localPath}{uri.Query}";

            using var httpRequest = new HttpRequestMessage {
                Method = new HttpMethod(request.Method!),
                RequestUri = new Uri(fullLocalUrl)
            };

            // Copy headers, filtering out ones that cause problems
            if (request.Headers != null)
            {
                foreach (var (key, value) in request.Headers)
                {
                    if (!key.StartsWith(':') &&
                        !string.Equals(key, "host", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "connection", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "content-length", StringComparison.OrdinalIgnoreCase))
                    {
                        httpRequest.Headers.TryAddWithoutValidation(key, value);
                    }
                }
            }

            // Add body if present
            if (request.Body != null && request.Body.Length > 0)
            {
                httpRequest.Content = new ByteArrayContent(request.Body);

                // Set Content-Type if available
                if (request.Headers != null && request.Headers.TryGetValue("Content-Type", out var contentType))
                {
                    httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
            }

            logger.LogInformation($"Forwarding request: {request.Method} {fullLocalUrl}");

            // Send the request with a timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await httpClient.SendAsync(httpRequest, cts.Token);

            // Create the tunnel response
            var tunnelResponse = new TunnelResponse {
                RequestId = request.RequestId,
                StatusCode = (int)response.StatusCode,
                Headers = new Dictionary<string, string>()
            };

            // Copy response headers
            foreach (var header in response.Headers)
            {
                tunnelResponse.Headers[header.Key] = string.Join(",", header.Value);
            }

            // Copy content headers
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    tunnelResponse.Headers[header.Key] = string.Join(",", header.Value);
                }

                // Get response body
                tunnelResponse.Body = await response.Content.ReadAsByteArrayAsync(cts.Token);
            }

            logger.LogInformation($"Received response ({fullLocalUrl}): {tunnelResponse.StatusCode}");
            return tunnelResponse;
        }
        catch (OperationCanceledException)
        {
            logger.LogError("Request timed out");
            return CreateErrorResponse(504, $"{caller}Gateway Timeout", "The local service did not respond in time", "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError($"HTTP Request error: {ex.Message}");

            int statusCode = ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : 503;
            return CreateErrorResponse(statusCode, $"{caller}Service Unavailable", "The local service is not responding", ex.Message);
        }
        catch (UriFormatException ex)
        {
            logger.LogError($"Invalid URL format: {ex.Message}");
            return CreateErrorResponse(400, $"{caller}Bad Request", "Invalid URL format", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError($"Unexpected error: {ex.Message}");
            return CreateErrorResponse(500, $"{caller}Internal Server Error", "An unexpected error occurred", ex.Message);
        }
    }

    /// <summary>
    /// Creates an error response with the specified details.
    /// </summary>
    private static TunnelResponse CreateErrorResponse(int statusCode, string error, string message, string details)
    {
        var errorObject = new {
            error,
            message,
            details,
            timestamp = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(errorObject, new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return new TunnelResponse {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    /// <summary>
    /// Gets a description for an HTTP status code.
    /// </summary>
    private static string GetStatusDescription(int statusCode)
    {
        return statusCode switch {
            200 => "OK",
            201 => "Created",
            202 => "Accepted",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            408 => "Request Timeout",
            409 => "Conflict",
            500 => "Internal Server Error",
            502 => "Bad Gateway",
            503 => "Service Unavailable",
            504 => "Gateway Timeout",
            _ => string.Empty
        };
    }
}