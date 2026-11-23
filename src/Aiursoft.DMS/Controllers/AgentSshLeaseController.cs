using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentSshLeaseController(TemplateDbContext db, ControllerGate gate, PersonalSshKeys keys, AccessService access) : ControllerBase
{
    [HttpPost("api/agent/ssh-leases/{taskId:guid}/authorize")]
    public Task<IActionResult> AuthorizeLease(Guid taskId, CancellationToken token) => gate.RunAsync<IActionResult>(async () =>
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var definition = OperationsWorkflows.SshOpen;
        var task = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.MachineId == machine
            && t.IsMachineTask && t.Origin == WorkflowOrigin.Manual && t.Definition == definition.Name
            && t.Version == definition.Version && t.State == RunState.Running, token);
        if (task == null) return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        var launch = await db.WorkflowLaunches.AsNoTracking().SingleOrDefaultAsync(x => x.RootId == task.RootId, token);
        if (launch == null || launch.Definition != definition.Name || launch.Version != definition.Version ||
            launch.Risk != definition.Risk || !launch.ReadTargets().SequenceEqual([machine]))
            return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        var approvedInput = launch.ReadParameters().Input;
        var input = definition.ReadInput(approvedInput);
        var approvedKeys = input.ApprovedKeys();
        if (approvedInput != task.Input || approvedKeys.Length == 0)
            return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        try
        {
            await access.PrincipalAsync(launch.RequesterId);
            var authorized = new List<SshAuthorizedKey>(approvedKeys.Length);
            foreach (var approved in approvedKeys)
            {
                var key = await keys.RequireVerifiedAsync(launch.RequesterId, approved.Id, approved.Fingerprint, token);
                authorized.Add(new SshAuthorizedKey(key.Id, key.PublicKey));
            }
            return Ok(input.Keys != null
                ? new SshLeaseAuthorization(Guid.Empty, launch.RequesterId, "") { Keys = authorized.ToArray() }
                : new SshLeaseAuthorization(authorized[0].Id, launch.RequesterId, authorized[0].PublicKey));
        }
        catch (DmsException error)
        {
            return StatusCode(error.StatusCode);
        }
    });
}
