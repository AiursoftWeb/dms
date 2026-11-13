using System.Net.WebSockets;
using System.Text.Json;

namespace Aiursoft.DMS.Contracts;

public static class WebSocketJson
{
    public const int MaximumMessageBytes = 128 * 1024;

    public static Task SendAsync(WebSocket socket, AgentMessage message, CancellationToken token) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(message)), WebSocketMessageType.Text, true, token);

    public static async Task<AgentMessage?> ReceiveAsync(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || stream.Length + result.Count > MaximumMessageBytes)
                throw new InvalidDataException("Invalid agent message.");
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<AgentMessage>(stream.ToArray()) ?? throw new InvalidDataException("Empty agent message.");
    }
}
