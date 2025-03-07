using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;

namespace PGrok.Common
{
    internal static class WebSocketHelpers
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
        /// <summary>
        /// Receives a complete message from a WebSocket and returns it as a string.
        /// Uses buffer pooling for better memory efficiency.
        /// </summary>
        /// <param name="webSocket">The WebSocket to receive from.</param>
        /// <param name="ms">A MemoryStream to use for buffering the message.</param>
        /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
        /// <returns>The received message as a bytes.</returns>
        public static async Task<WebSocketReceiveResult> ReceiveBytesAsync(this WebSocket webSocket, MemoryStream ms, CancellationToken cancellationToken = default)
        {
            if (webSocket.State != WebSocketState.Open)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.Empty,
                                            webSocket.CloseStatusDescription);
            }

            ms.SetLength(0);

            // Use a pooled buffer to reduce memory allocations
            byte[] buffer = s_bytePool.Rent(16384);
            int readBytes = 0;
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
                        return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, WebSocketCloseStatus.EndpointUnavailable,
                                            webSocket.CloseStatusDescription);
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
                        return new WebSocketReceiveResult(readBytes, WebSocketMessageType.Close, true, 
                                        WebSocketCloseStatus.NormalClosure,
                                            webSocket.CloseStatusDescription);
                    }

                    readBytes += result.Count;
                    await ms.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }
                while (!result.EndOfMessage);

                ms.Seek(0, SeekOrigin.Begin);
                return new WebSocketReceiveResult(readBytes, result.MessageType, result.EndOfMessage, 
                    result.CloseStatus, result.CloseStatusDescription);
            }
            finally
            {
                // Return the buffer to the pool
                s_bytePool.Return(buffer);
            }
        }
    }

}
