using Aiursoft.DMS.Entities;
using ClickHouse.Client.Utility;
using System.Data.Common;

namespace Aiursoft.DMS.Services.Dms;

public sealed partial class TraceDatabase
{
    public async Task InsertAuditAsync(WorkflowAuditOutbox row, CancellationToken token = default)
    {
        var item = row.Read();
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DmsWorkflowAudit (
                EventId, ControllerSequence, Kind, OccurredAt, RecordedAt,
                WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId, Location, MachineId, InstallationId,
                Definition, Version, RequesterId, ApproverId, AutomaticApproval, RequestedRisk, RequestedDefinition, RequestedVersion, Decision, DecidedAt, PreviousState, State, Origin, Trigger, ScheduleId, ScheduleExpression, ScheduledAt)
            VALUES (
                {event:String}, {sequence:Int64}, {kind:String}, {occurred:DateTime64(7)}, {recorded:DateTime64(7)},
                {workflow:Nullable(String)}, {root:Nullable(String)}, {parent:Nullable(String)}, {lockbox:Nullable(String)}, {location:Nullable(String)},
                {machine:Nullable(String)}, {installation:Nullable(String)}, {definition:String}, {version:String},
                {requester:Nullable(String)}, {approver:Nullable(String)}, {automatic:Nullable(Bool)}, {risk:Nullable(String)},
                {requestedDefinition:Nullable(String)}, {requestedVersion:Nullable(String)},
                {decision:Nullable(String)}, {decided:Nullable(DateTime64(7))}, {previous:Nullable(String)}, {state:Nullable(String)}, {origin:String}, {trigger:String}, {schedule:Nullable(String)}, {expression:Nullable(String)}, {scheduled:Nullable(DateTime64(7))})
            """;
        command.AddParameter("event", row.EventId.ToString()); command.AddParameter("sequence", row.Id);
        command.AddParameter("kind", item.Kind.ToString()); command.AddParameter("occurred", item.OccurredAt); command.AddParameter("recorded", item.RecordedAt);
        command.AddParameter("workflow", item.WorkflowId?.ToString()); command.AddParameter("root", item.RootWorkflowId?.ToString());
        command.AddParameter("parent", item.ParentWorkflowId?.ToString()); command.AddParameter("lockbox", item.LockboxId?.Value.ToString());
        command.AddParameter("location", item.Location?.ToString());
        command.AddParameter("machine", item.MachineId?.ToString()); command.AddParameter("installation", item.InstallationId?.ToString());
        command.AddParameter("definition", item.Definition); command.AddParameter("version", item.Version);
        command.AddParameter("requester", item.RequesterId); command.AddParameter("approver", item.ApproverId);
        command.AddParameter("automatic", item.AutomaticApproval); command.AddParameter("risk", item.RequestedRisk?.ToString());
        command.AddParameter("requestedDefinition", item.RequestedDefinition); command.AddParameter("requestedVersion", item.RequestedVersion);
        command.AddParameter("decision", item.Decision?.ToString()); command.AddParameter("decided", item.DecidedAt);
        command.AddParameter("previous", item.PreviousState?.ToString()); command.AddParameter("state", item.State?.ToString());
        command.AddParameter("origin", item.Origin.ToString()); command.AddParameter("trigger", item.Trigger.ToString());
        command.AddParameter("schedule", item.ScheduleId); command.AddParameter("expression", item.ScheduleExpression); command.AddParameter("scheduled", item.ScheduledAt);
        foreach (DbParameter parameter in command.Parameters) parameter.Value ??= DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }
}
