using System.Net.WebSockets;
using System.Text;

namespace PGrok.Common.Helpers;

public static class WebSocketHelpers
{
    public static async Task<string> ReceiveStringAsync(WebSocket webSocket, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();

        while (true)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            await ms.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task SendStringAsync(WebSocket webSocket, string data, CancellationToken cancellationToken = default)
    {
        var buffer = Encoding.UTF8.GetBytes(data);
        await webSocket.SendAsync(
            buffer,
            WebSocketMessageType.Text,
            WebSocketMessageFlags.EndOfMessage,
            cancellationToken
        );
    }
}
