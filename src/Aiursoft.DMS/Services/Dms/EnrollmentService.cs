using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class EnrollmentService(TemplateDbContext db, AccessService access, ControllerGate gate, WorkflowService workflows)
{
    private static JoinKeySummary Summary(JoinKey key) => new(key.Id, key.Name, key.IssuerId, key.ExpiresAt, key.Revoked);
    private static bool ValidSecret(string? key, string prefix) => key is { Length: 69 } && key.StartsWith(prefix, StringComparison.Ordinal) && key[5..].All(char.IsAsciiHexDigit);

    public Task<IssuedJoinKey> IssueAsync(string actor, JoinKeyInput input) => gate.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100 || input.Name.Any(char.IsControl) || input.Days is < 1 or > 365)
            throw new DmsException(400, "A key name and expiry of between 1 and 365 days are required.");
        var raw = "dmsj_" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var key = new JoinKey { IssuerId = actor, Name = input.Name.Trim(), KeyHash = DmsKeyAuthenticationHandler.Hash(raw), ExpiresAt = DateTime.UtcNow.AddDays(input.Days) };
        db.JoinKeys.Add(key); access.Audit(actor, "key.join.issue", key.Id, $"Expires={key.ExpiresAt:O}");
        await db.SaveChangesAsync(); return new IssuedJoinKey(Summary(key), raw);
    });

    public Task<List<JoinKeySummary>> KeysAsync(string actor) => gate.RunAsync(async () =>
    {
        var keys = await db.JoinKeys.AsNoTracking().NotCacheable().OrderBy(k => k.CreatedAt).ToListAsync();
        access.Audit(actor, "key.join.list"); await db.SaveChangesAsync(); return keys.Select(Summary).ToList();
    });

    public Task<JoinKeySummary> RevokeAsync(string actor, Guid id) => gate.RunAsync(async () =>
    {
        var key = await db.JoinKeys.NotCacheable().SingleOrDefaultAsync(k => k.Id == id) ?? throw new DmsException(404, "Join key not found.");
        key.Revoked = true; access.Audit(actor, "key.join.revoke", id); await db.SaveChangesAsync(); return Summary(key);
    });

    public Task<List<EnrollmentStatus>> ListAsync(string actor) => gate.RunAsync(async () =>
    {
        var items = await db.AgentEnrollments.NotCacheable().Include(e => e.JoinKey).Include(e => e.Lockbox)
            .OrderBy(e => e.CreatedAt).ToListAsync();
        var result = new List<EnrollmentStatus>();
        foreach (var item in items) result.Add(await StatusCoreAsync(item));
        access.Audit(actor, "enrollment.list"); await db.SaveChangesAsync(); return result;
    });

    public Task<EnrollmentStatus> SubmitAsync(Guid keyId, EnrollmentInput input) => gate.RunAsync(() =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(() => SubmitTransactionalAsync(keyId, input)));

    private async Task<EnrollmentStatus> SubmitTransactionalAsync(Guid keyId, EnrollmentInput input)
    {
        // A retry after a rolled-back transaction must not reuse tracked, partially saved entities.
        db.ChangeTracker.Clear();
        if (input.Id == Guid.Empty || input.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(input.MachineName) || input.MachineName.Length > 100 || input.MachineName.Any(char.IsControl)
            || !ValidSecret(input.ReceiptKey, "dmsr_") || !ValidSecret(input.AgentKey, "dmsa_")) throw new DmsException(400, "Invalid enrollment identity, name or credentials.");
        var key = await db.JoinKeys.NotCacheable().Include(k => k.Issuer).SingleOrDefaultAsync(k => k.Id == keyId)
            ?? throw new DmsException(401, "Invalid join key.");
        if (key.Revoked || key.ExpiresAt <= DateTime.UtcNow || key.Issuer.LockoutEnd > DateTimeOffset.UtcNow) throw new DmsException(403, "Join key is unavailable.");
        var receipt = DmsKeyAuthenticationHandler.Hash(input.ReceiptKey); var agent = DmsKeyAuthenticationHandler.Hash(input.AgentKey);
        var prior = await db.AgentEnrollments.NotCacheable().Include(e => e.JoinKey).Include(e => e.Lockbox).SingleOrDefaultAsync(e => e.Id == input.Id);
        if (prior != null)
        {
            if (prior.JoinKeyId != key.Id || prior.InstallationId != input.InstallationId || prior.MachineName != input.MachineName || prior.ReceiptHash != receipt || prior.AgentKeyHash != agent)
                throw new DmsException(409, "Enrollment ID already belongs to different parameters.");
            return await StatusCoreAsync(prior);
        }
        if (await db.Machines.NotCacheable().AnyAsync(m => m.InstallationId == input.InstallationId || m.KeyHash == agent)
            || await db.AgentEnrollments.NotCacheable().AnyAsync(e => e.ReceiptHash == receipt || e.AgentKeyHash == agent))
            throw new DmsException(409, "This installation or credential is already registered.");
        var attempts = await db.AgentEnrollments.NotCacheable().Include(e => e.JoinKey).Include(e => e.Lockbox).Where(e => e.InstallationId == input.InstallationId).ToListAsync();
        foreach (var attempt in attempts)
            if ((await StatusCoreAsync(attempt)).State is "PendingApproval" or "Registering") throw new DmsException(409, "This installation already has an active enrollment. Resume it using its receipt.");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var box = await workflows.SubmitCoreAsync(key.IssuerId, new OperationRequest
        {
            RequestId = input.Id, Workflow = WorkflowNames.AddMachine, MachineName = input.MachineName,
            Input = JsonSerializer.Serialize(new { EnrollmentId = input.Id, input.InstallationId }),
            Reason = $"Agent enrollment: {input.MachineName}; Installation={input.InstallationId}; JoinKey={key.Id}"
        });
        var item = new AgentEnrollment { Id = input.Id, JoinKeyId = key.Id, LockboxId = box.Id, InstallationId = input.InstallationId,
            MachineName = input.MachineName, ReceiptHash = receipt, AgentKeyHash = agent, MachineId = null, JoinKey = key, Lockbox = box };
        db.AgentEnrollments.Add(item); access.Audit(key.IssuerId, "enrollment.submit", item.Id, $"JoinKey={key.Id}; Lockbox={box.Id}; Installation={input.InstallationId}");
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return await StatusCoreAsync(item);
    }

    public Task<EnrollmentStatus> StatusAsync(Guid id) => gate.RunAsync(async () =>
    {
        var item = await db.AgentEnrollments.NotCacheable().Include(e => e.JoinKey).Include(e => e.Lockbox).SingleOrDefaultAsync(e => e.Id == id)
            ?? throw new DmsException(404, "Enrollment not found.");
        return await StatusCoreAsync(item);
    });

    private async Task<EnrollmentStatus> StatusCoreAsync(AgentEnrollment item)
    {
        var state = item.MachineId != null ? "Joined" : item.Lockbox.Decision == DecisionState.Denied ? "Denied"
            : item.JoinKey.Revoked || item.JoinKey.ExpiresAt <= DateTime.UtcNow ? "Unavailable"
            : item.Lockbox.Decision == DecisionState.Pending ? "PendingApproval" : "Registering";
        if (item.MachineId == null && item.Lockbox.WorkflowId is { } operation)
        {
            var run = await db.WorkflowRuns.AsNoTracking().NotCacheable().SingleAsync(r => r.Id == operation);
            if (run.State is RunState.Failed or RunState.Cancelled or RunState.NeedsVerification) state = "Failed";
        }
        return new EnrollmentStatus(item.Id, item.InstallationId, item.MachineName, item.LockboxId, item.Lockbox.WorkflowId, item.MachineId, state);
    }
}
