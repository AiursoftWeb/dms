using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Models.DmsViewModels;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class MachineSshOverview(TemplateDbContext db)
{
    public static readonly TimeSpan Freshness = TimeSpan.FromMinutes(6);

    public async Task<IReadOnlyList<DmsMachineSshRow>> ReadAsync(IReadOnlyList<DmsMachineRow> machines, CancellationToken token)
    {
        var online = machines.Where(machine => machine.Connection == MachineConnection.Online).Select(machine => machine.Id).ToArray();
        if (online.Length == 0) return machines.Select(machine => new DmsMachineSshRow(machine.Id)).ToList();

        var observed = await ReadObservationsAsync(online, token);
        var leaseIds = observed.Values.Where(status => status.LeaseId != null).Select(status => status.LeaseId!.Value).Distinct().ToArray();
        if (leaseIds.Length == 0)
            return machines.Select(machine => new DmsMachineSshRow(machine.Id, observed.GetValueOrDefault(machine.Id))).ToList();

        var leases = OperationsWorkflows.SshOpen.Name;
        var requests = await (from run in db.WorkflowRuns.AsNoTracking()
                join box in db.Lockboxes.AsNoTracking() on run.LockboxId equals box.Id
                where leaseIds.Contains(run.Id) && run.Definition == leases && run.State == RunState.Succeeded &&
                    box.Definition == leases && box.Decision == DecisionState.Approved
                select new { run.Id, run.MachineId, LockboxId = box.Id, box.RequesterId }).NotCacheable()
            .ToDictionaryAsync(item => item.Id, token);
        var requesterIds = requests.Values.Select(item => item.RequesterId).Distinct().ToArray();
        if (requesterIds.Length == 0)
            return machines.Select(machine => new DmsMachineSshRow(machine.Id, observed.GetValueOrDefault(machine.Id))).ToList();

        var people = await db.Users.AsNoTracking().NotCacheable().Where(person => requesterIds.Contains(person.Id))
            .Select(person => new { person.Id, person.DisplayName, person.UserName })
            .ToDictionaryAsync(person => person.Id, person => AccessService.PersonName(person.DisplayName, person.UserName), token);

        return machines.Select(machine =>
        {
            var status = observed.GetValueOrDefault(machine.Id);
            if (status?.LeaseId is { } leaseId && requests.TryGetValue(leaseId, out var request) && request.MachineId == machine.Id)
                return new DmsMachineSshRow(machine.Id, status, people.GetValueOrDefault(request.RequesterId), request.LockboxId);
            return new DmsMachineSshRow(machine.Id, status);
        }).ToList();
    }

    public async Task<SshLeaseReadiness> ReadReadinessAsync(Guid id, CancellationToken token)
    {
        var onlineAfter = DateTime.UtcNow.AddSeconds(-45);
        var online = await db.Machines.AsNoTracking().NotCacheable()
            .AnyAsync(machine => machine.Id == id && !machine.Disabled && machine.LastSeen > onlineAfter, token);
        if (!online) return SshLeaseReadiness.AgentDisconnected;

        var observed = await ReadObservationsAsync([id], token);
        return Classify(observed.GetValueOrDefault(id), online);
    }

    public static SshLeaseReadiness Classify(SshHostObservation? observation, bool online) => !online
        ? SshLeaseReadiness.AgentDisconnected
        : observation switch
        {
            { LeaseId: not null, ExpiresAtUtc: { } until } when until > DateTimeOffset.UtcNow => SshLeaseReadiness.InUse,
            { OrdinaryOpen: true } => SshLeaseReadiness.OrdinaryOpen,
            { BaselineReady: false } => SshLeaseReadiness.NeedsSetup,
            { BaselineReady: true, OrdinaryOpen: false, LeaseKnown: true, LeaseId: null } => SshLeaseReadiness.Ready,
            _ => SshLeaseReadiness.Unknown
        };

    private async Task<Dictionary<Guid, SshHostObservation>> ReadObservationsAsync(Guid[] online, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        var earliest = now.UtcDateTime - Freshness - TimeSpan.FromMinutes(3);
        var leases = OperationsWorkflows.SshOpen.Name;
        var disables = OperationsWorkflows.SshDisable.Name;
        var recoveries = OperationsWorkflows.SshRecover.Name;
        var checks = OperationsWorkflows.SshCheckLease.Name;
        var runs = await db.WorkflowRuns.AsNoTracking().NotCacheable()
            .Where(run => run.MachineId.HasValue && online.Contains(run.MachineId.Value) && run.CreatedAt >= earliest &&
                (run.Definition == leases || run.Definition == disables || run.Definition == recoveries || run.Definition == checks))
            .Select(run => new
            {
                MachineId = run.MachineId!.Value, run.Id, run.Definition, run.State, run.StartedAt, run.FinishedAt, run.Result
            })
            .ToListAsync(token);

        var observed = new Dictionary<Guid, SshHostObservation>();
        foreach (var group in runs.GroupBy(run => run.MachineId))
        {
            var ordered = group.Where(run => run.State is RunState.Succeeded or RunState.Warning)
                .Select(run => (run.Id, Status: Parse(run.Result, run.Definition == checks)))
                .Where(run => run.Status != null && run.Status.ObservedAtUtc >= now - Freshness &&
                    run.Status.ObservedAtUtc <= now.AddSeconds(30))
                .OrderByDescending(run => run.Status!.ObservedAtUtc).ThenByDescending(run => run.Id);
            var latest = ordered.FirstOrDefault().Status;
            if (latest == null || group.Any(run =>
                (run.State == RunState.Running && run.Definition != checks) ||
                (run.State == RunState.NeedsVerification && run.StartedAt is { } started &&
                    DateTime.SpecifyKind(run.FinishedAt ?? started, DateTimeKind.Utc) >= latest.ObservedAtUtc.UtcDateTime)))
                continue;
            observed.Add(group.Key, latest);
        }

        return observed;
    }

    private static SshHostObservation? Parse(string? result, bool leaseCheck)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        try
        {
            using var document = JsonDocument.Parse(result);
            var data = document.RootElement;
            if (!leaseCheck && (!data.TryGetProperty("Facts", out data) || data.ValueKind != JsonValueKind.Object))
                return null;
            if (!data.TryGetProperty(leaseCheck ? "Observation" : "SshObservation", out var item) ||
                item.ValueKind != JsonValueKind.Object)
                return null;
            var status = item.Deserialize<SshHostObservation>();
            return status != null && status.LeaseId.HasValue == status.ExpiresAtUtc.HasValue &&
                (status.LeaseId == null || status.LeaseKnown) ? status : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
