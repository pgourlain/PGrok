
using Microsoft.Extensions.Logging;
using PGrok.Common.Models;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BinaryReader = System.IO.BinaryReader;

namespace PGrok.Common.Helpers;

public static class HttpHelpers
{
    public const string RequestIdHeader = "X-PGrok-RequestId";
    public static Task<byte[]?> GetRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return null;

        using var reader = new BinaryReader(request.InputStream);
        return Task.FromResult(reader.ReadBytes(int.MaxValue))!;
    }

    public static Dictionary<string, string> GetHeaders(HttpListenerRequest request)
    {
        return request.Headers.AllKeys.ToDictionary(
            k => k ?? string.Empty,
            k => request.Headers[k] ?? string.Empty
        );
    }

    public static async Task Handle400(this HttpListenerResponse response, string message)
    {
        await HandleXXX(response, message, 400, true);        
    }
    public static Task Handle200(this HttpListenerResponse response, string message)
    {
        return HandleXXX(response, message, 200);        
    }

    public static async Task HandleXXX(this HttpListenerResponse response, string? message, int statusCode, bool closeStream = false)
    {
        response.StatusCode = statusCode;
        if (message != null)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            await response.OutputStream.WriteAsync(buffer);
        }
        if (closeStream)
        {
            response.Close();
        }
    }

    public static async Task HandleResponse(this HttpListenerResponse response, TunnelResponse? tunnelResponse, bool closeStream = true)
    {
        if (tunnelResponse == null) return;
        response.StatusCode = tunnelResponse.StatusCode;
        if (tunnelResponse.Headers != null)
        {
            foreach (var (key, value) in tunnelResponse.Headers)
            {
                if (key == HttpHelpers.RequestIdHeader) continue;
                response.Headers[key] = value;
            }
        }
        if (tunnelResponse.Body != null)
        {
            await response.OutputStream.WriteAsync(tunnelResponse.Body);
        }
        if (closeStream)
        {
            response.Close();
        }
    }

    internal static async Task<TunnelResponse> ForwardRequestToLocalService(TunnelRequest request, string tunnelId, string localBaseUrl, HttpClient httpClient, 
        ILogger logger, string caller = "")
    {
        try
        {
            var uri = new Uri(request.Url!);
            var segments = uri.AbsolutePath.Split(new[] { $"/{tunnelId}/" }, 2, StringSplitOptions.None);
            var localPath = segments.Length > 1 ? segments[1] : segments[0].TrimStart('/');

            var fullLocalUrl = $"{localBaseUrl}/{localPath}{uri.Query}";

            using var httpRequest = new HttpRequestMessage {
                Method = new HttpMethod(request.Method!),
                RequestUri = new Uri(fullLocalUrl)
            };

            // Copy headers
            if (request.Headers != null)
            {
                foreach (var (key, value) in request.Headers)
                {
                    if (!key.StartsWith(":") && key.ToLower() is not ("host" or "connection"))
                    {
                        httpRequest.Headers.TryAddWithoutValidation(key, value);
                    }
                }
            }

            // Add body if present
            if (request.Body != null)
            {
                httpRequest.Content = new ByteArrayContent(request.Body);
                if (request.Headers != null && request.Headers.TryGetValue("Content-Type", out var contentType))
                {
                    httpRequest.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                }
            }

            logger.LogInformation($"Forwarding request: {request.Method} {fullLocalUrl}");

            var response = await httpClient.SendAsync(httpRequest);

            return new TunnelResponse {
                StatusCode = (int)response.StatusCode,
                Headers = response.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)),
                Body = await response.Content.ReadAsByteArrayAsync()
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogError($"HTTP Request error: {ex.Message}");
            return CreateErrorResponse(503, $"{caller}Service Unavailable", "The local service is not responding", ex.Message);
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
    private static TunnelResponse CreateErrorResponse(int statusCode, string error, string message, string details)
    {
        return new TunnelResponse {
            StatusCode = statusCode,
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } },
            Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error, message, details }))
        };
    }
}
