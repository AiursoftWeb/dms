using System.Net.WebSockets;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
public class AgentChannelController(TemplateDbContext db, AgentSessions sessions, IServiceScopeFactory scopes, ILogger<AgentChannelController> logger) : ControllerBase
{
    [HttpGet("api/agent/channel")]
    public async Task Channel()
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest) { Response.StatusCode = 400; return; }
        var machineId = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var machine = await db.Machines.AsNoTracking().SingleAsync(m => m.Id == machineId);
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        using var session = sessions.Register(machineId, socket, machine.KeyHash);
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                var message = await WebSocketJson.ReceiveAsync(socket, timeout.Token);
                if (message == null) break;
                if (sessions.Get(machineId) != session) break;
                using var scope = scopes.CreateScope();
                var response = await scope.ServiceProvider.GetRequiredService<AgentEvents>().HandleAsync(machineId, session, message);
                if (response != null) await session.SendAsync(response, timeout.Token);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or DmsException or IOException)
        { logger.LogWarning("Agent {MachineId} disconnected: {ErrorType}.", machineId, exception.GetType().Name); }
        finally
        {
            if (sessions.Remove(machineId, session))
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AgentEvents>().DisconnectedAsync(machineId);
            }
        }
    }
}
