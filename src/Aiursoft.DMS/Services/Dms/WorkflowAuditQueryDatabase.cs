using Aiursoft.DMS.Contracts;
using System.Data.Common;
using System.Text.Json;

namespace Aiursoft.DMS.Services.Dms;

public sealed partial class TraceDatabase
{
    public Task<IReadOnlyList<WorkflowAuditRow>> ReadAuditAsync(WorkflowAuditQuery query, IReadOnlyList<Guid>? legacyTargetLockboxes = null)
    {
        var (predicates, values) = BuildPredicates(query, legacyTargetLockboxes);
        values["from"] = query.From!.Value.UtcDateTime;
        values["until"] = query.Until!.Value.UtcDateTime;
        values["skip"] = query.Page * 50;
        return QueryAsync("""
            SELECT EventId, ControllerSequence, Kind, OccurredAt, RecordedAt,
                   WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId,
                   Location, MachineId, InstallationId, Definition, Version,
                   RequesterId, ApproverId, AutomaticApproval, RequestedRisk, RequestedDefinition, RequestedVersion,
                   Decision, DecidedAt, PreviousState, State, Origin, Trigger, ScheduleId, ScheduleExpression, ScheduledAt, toJSONString(TargetMachineIds)
            FROM DmsWorkflowAudit FINAL WHERE
            """ + " " + string.Join(" AND ", predicates) + " ORDER BY RecordedAt DESC, ControllerSequence DESC, EventId DESC LIMIT 51 OFFSET {skip:UInt32}", Read, values);
    }

    // Only fixed column names enter SQL. All user-supplied values are bound parameters.
    internal static (List<string> Predicates, Dictionary<string, object?> Values) BuildPredicates(WorkflowAuditQuery query, IReadOnlyList<Guid>? legacyTargetLockboxes = null)
    {
        var predicates = new List<string> { "RecordedAt >= {from:DateTime64(7)}", "RecordedAt < {until:DateTime64(7)}" };
        var values = new Dictionary<string, object?>();
        if (legacyTargetLockboxes != null && query.MachineId != null)
        {
            predicates.Add("(has(TargetMachineIds, {machine:String}) OR LockboxId IN {targetLockboxes:Array(String)})");
            values["machine"] = query.MachineId.Value.ToString();
            values["targetLockboxes"] = legacyTargetLockboxes.Select(id => id.ToString()).ToArray();
        }
        else Add("machine", "MachineId", query.MachineId?.ToString());
        Add("workflow", "WorkflowId", query.WorkflowId?.ToString());
        Add("root", "RootWorkflowId", query.RootWorkflowId?.ToString()); Add("lockbox", "LockboxId", query.LockboxId?.Value.ToString());
        Add("requester", "RequesterId", query.RequesterId); Add("approver", "ApproverId", query.ApproverId);
        Add("definition", "Definition", query.Definition); Add("requested", "RequestedDefinition", query.RequestedDefinition);
        Add("origin", "Origin", query.Origin?.ToString()); Add("trigger", "Trigger", query.Trigger?.ToString());
        Add("risk", "RequestedRisk", query.Risk?.ToString());
        Add("kind", "Kind", query.Kind?.ToString()); Add("state", "State", query.State?.ToString());
        Add("decision", "Decision", query.Decision?.ToString());
        if (query.Kinds is { Count: > 0 } kinds)
        {
            var names = kinds.Select(kind => kind.ToString()).ToArray();
            predicates.Add("Kind IN (" + string.Join(", ", names.Select((_, index) => $"{{kind{index}:String}}")) + ")");
            for (var index = 0; index < names.Length; index++) values[$"kind{index}"] = names[index];
        }
        if (query.AutomaticApproval != null)
        {
            predicates.Add("AutomaticApproval = {automatic:Bool}"); values["automatic"] = query.AutomaticApproval.Value;
        }
        return (predicates, values);

        void Add(string key, string column, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            predicates.Add(column + " = {" + key + ":String}"); values[key] = value;
        }
    }

    private static WorkflowAuditRow Read(DbDataReader row)
    {
        string? Text(int i) => row.IsDBNull(i) ? null : row.GetString(i);
        Guid? Id(int i) => row.IsDBNull(i) ? null : Guid.Parse(row.GetString(i));
        T? Value<T>(int i) where T : struct, Enum => row.IsDBNull(i) ? null : Enum.Parse<T>(row.GetString(i));
        DateTime Time(int i) => DateTime.SpecifyKind(row.GetDateTime(i), DateTimeKind.Utc);
        return new WorkflowAuditRow(Guid.Parse(row.GetString(0)), row.GetInt64(1), new WorkflowAuditEntry(
            Enum.Parse<WorkflowAuditKind>(row.GetString(2)), Time(3), Time(4),
            Id(5), Id(6), Id(7), Id(8),
            Value<WorkflowLocation>(9), Id(10), Id(11), row.GetString(12), row.GetString(13),
            Text(14), Text(15), row.IsDBNull(16) ? null : row.GetBoolean(16), Value<RiskLevel>(17), Text(18), Text(19),
            Value<DecisionState>(20), row.IsDBNull(21) ? null : Time(21), Value<RunState>(22), Value<RunState>(23), Enum.Parse<WorkflowOrigin>(row.GetString(24)), Enum.Parse<WorkflowTrigger>(row.GetString(25)),
            Text(26), Text(27), row.IsDBNull(28) ? null : Time(28), JsonSerializer.Deserialize<Guid[]>(row.GetString(29))));
    }
}
