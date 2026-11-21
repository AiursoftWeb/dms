using System.Collections.Concurrent;
using System.Net.WebSockets;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AgentSession(WebSocket socket, string keyHash) : IDisposable
{
    private readonly SemaphoreSlim _send = new(1, 1);
    public string KeyHash => keyHash;
    public bool Ready { get; set; }
    public bool IsOpen => socket.State == WebSocketState.Open;
    public void Abort() => socket.Abort();
    public async Task SendAsync(AgentMessage message, CancellationToken token)
    {
        await _send.WaitAsync(token);
        try { await WebSocketJson.SendAsync(socket, message, token); }
        finally { _send.Release(); }
    }
    public void Dispose() { socket.Dispose(); _send.Dispose(); }
}

public sealed class AgentSessions
{
    private readonly ConcurrentDictionary<Guid, AgentSession> _sessions = new();
    public AgentSession Register(Guid machine, WebSocket socket, string keyHash)
    {
        var session = new AgentSession(socket, keyHash);
        if (_sessions.TryGetValue(machine, out var previous)) previous.Abort();
        _sessions[machine] = session;
        return session;
    }
    public bool Remove(Guid machine, AgentSession session) => _sessions.TryRemove(new KeyValuePair<Guid, AgentSession>(machine, session));
    public AgentSession? Get(Guid machine) => _sessions.GetValueOrDefault(machine);
}
