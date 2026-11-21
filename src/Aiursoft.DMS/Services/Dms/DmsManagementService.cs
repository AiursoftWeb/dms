using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

// Web and API management entry points require CanManageDmsAccess before calling this service.
public sealed class DmsManagementService(TemplateDbContext db, AccessService access, ControllerGate gate)
{
    public Task<AccessSnapshot> SnapshotAsync(string actor) => gate.RunAsync(async () =>
    {
        var result = new AccessSnapshot(
            await db.Machines.AsNoTracking().NotCacheable().OrderBy(m => m.Name).Select(m => new AccessMachine(m.Id, m.Name, m.Disabled)).ToListAsync());
        access.Audit(actor, "access.inspect"); await db.SaveChangesAsync(); return result;
    });

    public Task<AgentKeyResult> AgentKeyAsync(string actor, Guid id) => RotateAgentKeyAsync(actor, id, null);

    public Task<AgentKeyResult> RotateConfirmedAgentKeyAsync(string actor, Guid id, string expectedKeyHash, string confirmedName) =>
        RotateAgentKeyAsync(actor, id, new AgentKeyConfirmation(expectedKeyHash, confirmedName));

    private Task<AgentKeyResult> RotateAgentKeyAsync(string actor, Guid id, AgentKeyConfirmation? confirmation) => gate.RunAsync(async () =>
    {
        var machine = await db.Machines.NotCacheable().SingleOrDefaultAsync(m => m.Id == id) ?? throw new DmsException(404, "Machine not found.");
        if (machine.Disabled) throw new DmsException(409, "The machine is disabled.");
        if (confirmation != null && (confirmation.ExpectedKeyHash != machine.KeyHash || confirmation.Name != machine.Name))
            throw new DmsException(409, "The machine or its key changed. Review the replacement again.");
        var key = DmsKeyAuthenticationHandler.CreateKey(true); machine.KeyHash = DmsKeyAuthenticationHandler.Hash(key);
        access.Audit(actor, "key.agent.rotate", id); await db.SaveChangesAsync(); return new AgentKeyResult(id, key);
    });

    private sealed record AgentKeyConfirmation(string ExpectedKeyHash, string Name);
}
