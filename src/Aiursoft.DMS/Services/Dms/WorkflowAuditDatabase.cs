using Aiursoft.DMS.Entities;
using ClickHouse.Client.Utility;
using System.Data.Common;

namespace Aiursoft.DMS.Services.Dms;

public sealed partial class TraceDatabase
{
    public async Task InitializeAuditAsync(CancellationToken token = default)
    {
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS DmsWorkflowAudit (
                EventId String, ControllerSequence Int64, Kind String,
                OccurredAt DateTime64(7, 'UTC'), RecordedAt DateTime64(7, 'UTC'),
                WorkflowId String, RootWorkflowId String, ParentWorkflowId Nullable(String), LockboxId String, ClusterId String,
                Location String, MachineId Nullable(String), InstallationId Nullable(String), Definition String, Version String,
                RequesterId Nullable(String), ApproverId Nullable(String), AutomaticApproval Nullable(Bool), RequestedRisk Nullable(String),
                RequestedDefinition Nullable(String), RequestedVersion Nullable(String),
                Decision Nullable(String), DecidedAt Nullable(DateTime64(7, 'UTC')), PreviousState Nullable(String), State Nullable(String)
            ) ENGINE = ReplacingMergeTree ORDER BY (ClusterId, WorkflowId, EventId)
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task InsertAuditAsync(WorkflowAuditOutbox row, CancellationToken token = default)
    {
        var item = row.Read();
        var connection = await GetConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO DmsWorkflowAudit (
                EventId, ControllerSequence, Kind, OccurredAt, RecordedAt,
                WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId, ClusterId, Location, MachineId, InstallationId,
                Definition, Version, RequesterId, ApproverId, AutomaticApproval, RequestedRisk, RequestedDefinition, RequestedVersion, Decision, DecidedAt, PreviousState, State)
            VALUES (
                {event:String}, {sequence:Int64}, {kind:String}, {occurred:DateTime64(7)}, {recorded:DateTime64(7)},
                {workflow:String}, {root:String}, {parent:Nullable(String)}, {lockbox:String}, {cluster:String}, {location:String},
                {machine:Nullable(String)}, {installation:Nullable(String)}, {definition:String}, {version:String},
                {requester:Nullable(String)}, {approver:Nullable(String)}, {automatic:Nullable(Bool)}, {risk:Nullable(String)},
                {requestedDefinition:Nullable(String)}, {requestedVersion:Nullable(String)},
                {decision:Nullable(String)}, {decided:Nullable(DateTime64(7))}, {previous:Nullable(String)}, {state:Nullable(String)})
            """;
        command.AddParameter("event", row.EventId.ToString()); command.AddParameter("sequence", row.Id);
        command.AddParameter("kind", item.Kind.ToString()); command.AddParameter("occurred", item.OccurredAt); command.AddParameter("recorded", item.RecordedAt);
        command.AddParameter("workflow", item.WorkflowId.ToString()); command.AddParameter("root", item.RootWorkflowId.ToString());
        command.AddParameter("parent", item.ParentWorkflowId?.ToString()); command.AddParameter("lockbox", item.LockboxId.ToString());
        command.AddParameter("cluster", item.ClusterId.ToString()); command.AddParameter("location", item.Location.ToString());
        command.AddParameter("machine", item.MachineId?.ToString()); command.AddParameter("installation", item.InstallationId?.ToString());
        command.AddParameter("definition", item.Definition); command.AddParameter("version", item.Version);
        command.AddParameter("requester", item.RequesterId); command.AddParameter("approver", item.ApproverId);
        command.AddParameter("automatic", item.AutomaticApproval); command.AddParameter("risk", item.RequestedRisk?.ToString());
        command.AddParameter("requestedDefinition", item.RequestedDefinition); command.AddParameter("requestedVersion", item.RequestedVersion);
        command.AddParameter("decision", item.Decision?.ToString()); command.AddParameter("decided", item.DecidedAt);
        command.AddParameter("previous", item.PreviousState?.ToString()); command.AddParameter("state", item.State?.ToString());
        foreach (DbParameter parameter in command.Parameters) parameter.Value ??= DBNull.Value;
        await command.ExecuteNonQueryAsync(token);
    }
}
