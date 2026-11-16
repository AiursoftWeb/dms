namespace Aiursoft.DMS.Services.Dms;

public sealed partial class TraceDatabase
{
    private static readonly SemaphoreSlim AuditSchemaGate = new(1, 1);
    private const string AuditColumns = "EventId, ControllerSequence, Kind, OccurredAt, RecordedAt, WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId, Location, MachineId, InstallationId, Definition, Version, RequesterId, ApproverId, AutomaticApproval, RequestedRisk, RequestedDefinition, RequestedVersion, Decision, DecidedAt, PreviousState, State";
    private const string AuditSchema = """
        (EventId String, ControllerSequence Int64, Kind String,
         OccurredAt DateTime64(7, 'UTC'), RecordedAt DateTime64(7, 'UTC'),
         WorkflowId String, RootWorkflowId String, ParentWorkflowId Nullable(String), LockboxId String,
         Location String, MachineId Nullable(String), InstallationId Nullable(String), Definition String, Version String,
         RequesterId Nullable(String), ApproverId Nullable(String), AutomaticApproval Nullable(Bool), RequestedRisk Nullable(String),
         RequestedDefinition Nullable(String), RequestedVersion Nullable(String),
         Decision Nullable(String), DecidedAt Nullable(DateTime64(7, 'UTC')), PreviousState Nullable(String), State Nullable(String))
        ENGINE = ReplacingMergeTree ORDER BY (WorkflowId, EventId)
        """;

    // One Controller owns this database. Its old process must be stopped before upgrading.
    // EXCHANGE is the commit point: interruption before it recopies the original; after it only cleanup remains.
    public async Task InitializeAuditAsync(CancellationToken token = default)
    {
        await AuditSchemaGate.WaitAsync(token);
        try
        {
            var connection = await GetConnection();
            await ExecuteAsync("CREATE TABLE IF NOT EXISTS DmsWorkflowAudit " + AuditSchema);
            var oldSchema = await HasLegacyColumnAsync("DmsWorkflowAudit");
            var stagingExists = Convert.ToUInt64(await ScalarAsync("EXISTS TABLE DmsWorkflowAuditMigration")) != 0;
            if (!oldSchema)
            {
                if (stagingExists)
                {
                    if (!await HasLegacyColumnAsync("DmsWorkflowAuditMigration"))
                        throw new InvalidDataException("Unexpected audit migration table. Preserve it for investigation.");
                    await ExecuteAsync("DROP TABLE DmsWorkflowAuditMigration SYNC");
                }
                return;
            }
            var engine = Convert.ToString(await ScalarAsync("SELECT engine FROM system.databases WHERE name = currentDatabase()"));
            if (engine is not ("Atomic" or "Shared"))
                throw new InvalidDataException("Audit migration requires an Atomic or Shared ClickHouse database.");
            if (stagingExists && await HasLegacyColumnAsync("DmsWorkflowAuditMigration"))
                throw new InvalidDataException("Unexpected legacy staging table. Preserve both audit tables for investigation.");
            await ExecuteAsync("CREATE TABLE IF NOT EXISTS DmsWorkflowAuditMigration " + AuditSchema);
            await ExecuteAsync("TRUNCATE TABLE DmsWorkflowAuditMigration");
            await ExecuteAsync($"INSERT INTO DmsWorkflowAuditMigration ({AuditColumns}) SELECT {AuditColumns} FROM DmsWorkflowAudit FINAL");
            var original = await FingerprintAsync("DmsWorkflowAudit");
            var copied = await FingerprintAsync("DmsWorkflowAuditMigration");
            if (original != copied || original.Count != original.Events)
                throw new InvalidDataException("Audit migration verification failed. The original table remains authoritative.");
            await ExecuteAsync("EXCHANGE TABLES DmsWorkflowAudit AND DmsWorkflowAuditMigration");
            await ExecuteAsync("DROP TABLE DmsWorkflowAuditMigration SYNC");

            async Task ExecuteAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(token);
            }
            async Task<object?> ScalarAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                return await command.ExecuteScalarAsync(token);
            }
            async Task<bool> HasLegacyColumnAsync(string table) => Convert.ToUInt64(await ScalarAsync(
                $"SELECT count() FROM system.columns WHERE database = currentDatabase() AND table = '{table}' AND name = 'ClusterId'")) != 0;
            async Task<(ulong Count, ulong Events, ulong Fingerprint)> FingerprintAsync(string table)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT count(), uniqExact(EventId), groupBitXor(cityHash64(tuple({AuditColumns}))) FROM {table} FINAL";
                await using var reader = await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                return (Convert.ToUInt64(reader.GetValue(0)), Convert.ToUInt64(reader.GetValue(1)), Convert.ToUInt64(reader.GetValue(2)));
            }
        }
        finally { AuditSchemaGate.Release(); }
    }
}
