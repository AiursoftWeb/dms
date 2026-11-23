using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Controllers;

[Authorize(AuthenticationSchemes = DmsKeyAuthenticationHandler.SchemeName, Policy = "DmsAgent")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AgentFrpsDeployController(TemplateDbContext db, IDataProtectionProvider protection) : ControllerBase
{
    [HttpPost("api/agent/frps-deploy/{taskId:guid}/token")]
    public async Task<IActionResult> ReadToken(Guid taskId, CancellationToken token)
    {
        var machine = Guid.Parse(User.FindFirst("DmsMachine")!.Value);
        var definition = MachineWorkflows.FrpsDeploy;
        var task = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(t => t.Id == taskId && t.MachineId == machine
            && t.IsMachineTask && t.Origin == WorkflowOrigin.Manual && t.Definition == definition.Name
            && t.Version == definition.Version && t.State == RunState.Running, token);
        if (task == null) return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        var launch = await db.WorkflowLaunches.AsNoTracking().SingleOrDefaultAsync(x => x.RootId == task.RootId, token);
        if (launch == null || launch.Definition != definition.Name || launch.Version != definition.Version ||
            launch.Risk != definition.Risk || !launch.ReadTargets().SequenceEqual([machine]) ||
            launch.ReadParameters().Input != task.Input)
            return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        if (definition.ReadInput(task.Input).ProtectedToken is not { } encrypted)
            return Forbid(DmsKeyAuthenticationHandler.SchemeName);

        return Ok(new FrpsDeployToken(protection.CreateProtector(FrpsDeployInput.ProtectionPurpose).Unprotect(encrypted)));
    }
}
