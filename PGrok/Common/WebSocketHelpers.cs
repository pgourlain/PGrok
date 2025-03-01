using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace PGrok.Common.Helpers;

public static class WebSocketHelpers
{
    private static readonly ArrayPool<byte> s_bytePool = ArrayPool<byte>.Shared;

    /// <summary>
    /// Receives a complete message from a WebSocket and returns it as a string.
    /// Uses buffer pooling for better memory efficiency.
    /// </summary>
    /// <param name="webSocket">The WebSocket to receive from.</param>
    /// <param name="ms">A MemoryStream to use for buffering the message.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>The received message as a string.</returns>
    public static async Task<string> ReceiveStringAsync(WebSocket webSocket, MemoryStream ms, CancellationToken cancellationToken = default)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return string.Empty;
        }

        ms.SetLength(0);

        // Use a pooled buffer to reduce memory allocations
        byte[] buffer = s_bytePool.Rent(8192);

        try
        {
            WebSocketReceiveResult? result = null;

            do
            {
                try
                {
                    result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        cancellationToken);
                }
                catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                {
                    // Connection was closed before we could receive a complete message
                    return string.Empty;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Honor the close handshake
                    if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
                    {
                        await webSocket.CloseOutputAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing in response to peer close",
                            cancellationToken);
                    }

                    return string.Empty;
                }

                await ms.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            }
            while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);

            // Only create a string for text messages
            if (result.MessageType == WebSocketMessageType.Text)
            {
                return Encoding.UTF8.GetString(ms.ToArray());
            }

            return string.Empty;
        }
        finally
        {
            // Return the buffer to the pool
            s_bytePool.Return(buffer);
        }
    }

    /// <summary>
    /// Sends a string message over a WebSocket.
    /// </summary>
    /// <param name="webSocket">The WebSocket to send to.</param>
    /// <param name="data">The string data to send.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    public static async Task SendStringAsync(WebSocket webSocket, string data, CancellationToken cancellationToken = default)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var buffer = Encoding.UTF8.GetBytes(data);

        // For large messages, we should chunk the data
        const int chunkSize = 4096;

        // If message is small, send it all at once
        if (buffer.Length <= chunkSize)
        {
            await webSocket.SendAsync(
                buffer,
                WebSocketMessageType.Text,
                WebSocketMessageFlags.EndOfMessage,
                cancellationToken
            );

            return;
        }

        // For larger messages, chunk them
        int offset = 0;

        while (offset < buffer.Length)
        {
            int count = Math.Min(chunkSize, buffer.Length - offset);
            bool isEndOfMessage = (offset + count) >= buffer.Length;

            await webSocket.SendAsync(
                new ArraySegment<byte>(buffer, offset, count),
                WebSocketMessageType.Text,
                isEndOfMessage ? WebSocketMessageFlags.EndOfMessage : WebSocketMessageFlags.None,
                cancellationToken
            );

            offset += count;
        }
    }

    /// <summary>
    /// Sends binary data over a WebSocket.
    /// </summary>
    /// <param name="webSocket">The WebSocket to send to.</param>
    /// <param name="data">The binary data to send.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    public static async Task SendBinaryAsync(WebSocket webSocket, byte[] data, CancellationToken cancellationToken = default)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        // For large messages, we should chunk the data
        const int chunkSize = 4096;

        // If message is small, send it all at once
        if (data.Length <= chunkSize)
        {
            await webSocket.SendAsync(
                data,
                WebSocketMessageType.Binary,
                WebSocketMessageFlags.EndOfMessage,
                cancellationToken
            );

            return;
        }

        // For larger messages, chunk them
        int offset = 0;

        while (offset < data.Length)
        {
            int count = Math.Min(chunkSize, data.Length - offset);
            bool isEndOfMessage = (offset + count) >= data.Length;

            await webSocket.SendAsync(
                new ArraySegment<byte>(data, offset, count),
                WebSocketMessageType.Binary,
                isEndOfMessage ? WebSocketMessageFlags.EndOfMessage : WebSocketMessageFlags.None,
                cancellationToken
            );

            offset += count;
        }
    }

    /// <summary>
    /// Closes a WebSocket connection gracefully with a timeout.
    /// </summary>
    /// <param name="webSocket">The WebSocket to close.</param>
    /// <param name="status">The WebSocket close status.</param>
    /// <param name="description">A description of why the connection is being closed.</param>
    /// <param name="timeout">How long to wait for the close to complete.</param>
    /// <returns>True if the close completed within the timeout, false otherwise.</returns>
    public static async Task<bool> CloseGracefullyAsync(
        WebSocket webSocket,
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string description = "Connection closed",
        TimeSpan? timeout = null)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return true;
        }

        timeout ??= TimeSpan.FromSeconds(5);

        using var cts = new CancellationTokenSource(timeout.Value);

        try
        {
            await webSocket.CloseAsync(status, description, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred
            return false;
        }
        catch
        {
            // Other error occurred
            return false;
        }
    }
}