using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class WorkflowInvestigation(TemplateDbContext db, AccessService access, TraceDatabase database, ILogger<WorkflowInvestigation> logger)
{
    public async Task<WorkflowAuditPage> ReadAsync(string actor, WorkflowAuditQuery query)
    {
        // Check global read permission before revealing archive availability or accepting filters.
        await access.RequireViewAsync(actor);
        var until = query.Until?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var from = query.From?.ToUniversalTime() ?? until.AddDays(-1);
        if (from < DateTimeOffset.UnixEpoch || until.Year > 2261 || from >= until || until - from > TimeSpan.FromDays(31) || query.Page is < 0 or > 10000 ||
            (query.Origin != null && !Enum.IsDefined(query.Origin.Value)) ||
            (query.Trigger != null && !Enum.IsDefined(query.Trigger.Value)) ||
            (query.Risk != null && !Enum.IsDefined(query.Risk.Value)) ||
            (query.Decision != null && !Enum.IsDefined(query.Decision.Value)) ||
            (query.Kind != null && !Enum.IsDefined(query.Kind.Value)) || (query.State != null && !Enum.IsDefined(query.State.Value)) ||
            (query.Kinds != null && query.Kinds.Any(kind => !Enum.IsDefined(kind))) ||
            new[] { query.RequesterId, query.ApproverId, query.Definition, query.RequestedDefinition }.Any(s => s?.Length > 200))
            throw new DmsException(400, "Choose a valid time range of at most 31 days, page, and investigation filters.");
        query = query with { From = from, Until = until };
        access.Audit(actor, "operation.audit-query", detail: $"From={from:O}; Until={until:O}; Page={query.Page}");
        await db.SaveChangesAsync();
        return await QueryArchiveAsync(query);
    }

    public async Task<HistoryPage> ReadHistoryAsync(string actor, bool lockboxScope, HistoryQuery query)
    {
        // History is open to every authenticated human key: facts exclude parameters,
        // results, free-text reasons and credentials. Only the query itself is audited.
        var until = query.Until?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var from = query.From?.ToUniversalTime() ?? until.AddDays(-7);
        if (from < DateTimeOffset.UnixEpoch || until.Year > 2261 || from >= until || until - from > TimeSpan.FromDays(31) || query.Page is < 0 or > 10000 ||
            (query.Risk != null && !Enum.IsDefined(query.Risk.Value)) ||
            (query.Decision != null && !Enum.IsDefined(query.Decision.Value)) ||
            (query.State != null && !Enum.IsDefined(query.State.Value)) ||
            (query.Origin != null && !Enum.IsDefined(query.Origin.Value)) ||
            (query.Trigger != null && !Enum.IsDefined(query.Trigger.Value)) ||
            new[] { query.Requester, query.Approver, query.Machine, query.Definition }.Any(s => s?.Length > 200))
            throw new DmsException(400, "Choose a valid time range of at most 31 days, page, and history filters.");
        query = query with { From = from, Until = until };
        var requester = await ResolveUserAsync(query.Requester, "requester");
        var approver = await ResolveUserAsync(query.Approver, "approver");
        var machine = await ResolveMachineAsync(query.Machine);
        var auditQuery = new WorkflowAuditQuery
        {
            From = from, Until = until, Page = query.Page,
            Kinds = lockboxScope
                ? [WorkflowAuditKind.LockboxSubmitted, WorkflowAuditKind.LockboxApproved, WorkflowAuditKind.LockboxDenied, WorkflowAuditKind.LockboxWithdrawn]
                : [WorkflowAuditKind.WorkflowCreated, WorkflowAuditKind.WorkflowStateChanged],
            RequesterId = requester, ApproverId = approver, MachineId = machine,
            Definition = query.Definition, Risk = query.Risk, Decision = query.Decision,
            AutomaticApproval = query.AutomaticApproval, State = query.State,
            Origin = query.Origin, Trigger = query.Trigger,
            LockboxId = query.LockboxId, WorkflowId = query.WorkflowId, RootWorkflowId = query.RootWorkflowId
        };
        access.Audit(actor, lockboxScope ? "lockbox.history-query" : "workflow.history-query",
            detail: $"From={from:O}; Until={until:O}; Page={query.Page}");
        await db.SaveChangesAsync();
        return await QueryArchiveAsync(auditQuery, query);
    }

    private async Task<WorkflowAuditPage> QueryArchiveAsync(WorkflowAuditQuery query)
    {
        if (!database.Enabled) throw new DmsException(503, "ClickHouse is not configured. Investigation events remain in the durable backlog.");
        try
        {
            await database.InitializeAuditAsync();
            var rows = await database.ReadAuditAsync(query);
            return new WorkflowAuditPage(query, rows.Take(50).ToList(), rows.Count > 50);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Investigation archive query is unavailable.");
            throw new DmsException(503, "The investigation archive is temporarily unavailable. Retry later; pending events remain durable.");
        }
    }

    private async Task<HistoryPage> QueryArchiveAsync(WorkflowAuditQuery query, HistoryQuery echo)
    {
        if (!database.Enabled) throw new DmsException(503, "ClickHouse is not configured. Investigation events remain in the durable backlog.");
        try
        {
            await database.InitializeAuditAsync();
            var rows = await database.ReadAuditAsync(query);
            var page = rows.Take(50).ToList();
            return new HistoryPage(await EnrichAsync(page), echo, rows.Count > 50);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Investigation archive query is unavailable.");
            throw new DmsException(503, "The investigation archive is temporarily unavailable. Retry later; pending events remain durable.");
        }
    }

    private async Task<IReadOnlyList<HistoryRow>> EnrichAsync(List<WorkflowAuditRow> rows)
    {
        var userIds = rows.SelectMany(row => new[] { row.Fact.RequesterId, row.Fact.ApproverId })
            .OfType<string>().Distinct().ToArray();
        var machineIds = rows.Select(row => row.Fact.MachineId).OfType<Guid>().Distinct().ToArray();
        var users = userIds.Length == 0 ? new Dictionary<string, string>() :
            await db.Users.AsNoTracking().Where(user => userIds.Contains(user.Id))
                .ToDictionaryAsync(user => user.Id, user => AccessService.PersonName(user.DisplayName, user.UserName));
        var machines = machineIds.Length == 0 ? new Dictionary<Guid, string>() :
            await db.Machines.AsNoTracking().Where(machine => machineIds.Contains(machine.Id))
                .ToDictionaryAsync(machine => machine.Id, machine => machine.Name);
        return rows.Select(row => new HistoryRow(row.EventId, row.ControllerSequence, row.Fact,
            row.Fact.RequesterId != null && users.TryGetValue(row.Fact.RequesterId, out var requester) ? requester : null,
            row.Fact.ApproverId != null && users.TryGetValue(row.Fact.ApproverId, out var approver) ? approver : null,
            row.Fact.MachineId != null && machines.TryGetValue(row.Fact.MachineId.Value, out var machine) ? machine : null)).ToList();
    }

    private async Task<string?> ResolveUserAsync(string? value, string role)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParse(value, out var id)) return id.ToString();
        var name = value.Trim();
        var matches = await db.Users.AsNoTracking().NotCacheable()
            .Where(user => user.UserName!.ToLower() == name.ToLower() || user.DisplayName!.ToLower() == name.ToLower())
            .Select(user => user.Id).Distinct().ToListAsync();
        if (matches.Count == 0) throw new DmsException(400, $"No user matches '{name}'. Pass a username, display name, or user ID.");
        if (matches.Count > 1) throw new DmsException(400, $"Multiple users match '{name}'. Pass the user ID instead.");
        return matches[0];
    }

    private async Task<Guid?> ResolveMachineAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParse(value, out var id)) return id;
        var name = value.Trim();
        var matches = await db.Machines.AsNoTracking().NotCacheable()
            .Where(machine => machine.Name.ToLower() == name.ToLower())
            .Select(machine => machine.Id).Distinct().ToListAsync();
        if (matches.Count == 0) throw new DmsException(400, $"No machine named '{name}'. Pass a machine name or machine ID.");
        if (matches.Count > 1) throw new DmsException(400, $"Multiple machines are named '{name}'. Pass the machine ID instead.");
        return matches[0];
    }
}
