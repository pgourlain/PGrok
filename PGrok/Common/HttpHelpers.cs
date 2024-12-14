
using System.Net;

namespace PGrok.Common.Helpers;

public static class HttpHelpers
{
    public static async Task<string?> GetRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return null;

        using var reader = new StreamReader(request.InputStream);
        return await reader.ReadToEndAsync();
    }

    public static Dictionary<string, string> GetHeaders(HttpListenerRequest request)
    {
        return request.Headers.AllKeys.ToDictionary(
            k => k ?? string.Empty,
            k => request.Headers[k] ?? string.Empty
        );
    }
}
