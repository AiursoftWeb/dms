using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Aiursoft.DMS.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class ClusterRemovalMigrationTests
{
    [TestMethod]
    public async Task SqliteUpgradePreservesIdentityDecisionsTargetsAndUndeliveredAudit()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var db = new SqliteContext(new DbContextOptionsBuilder<SqliteContext>().UseSqlite(connection).Options);
        await VerifyUpgradeAsync(db);
    }

    // The same business fixture is also executed against a real isolated MySQL server during release acceptance.
    public static async Task VerifyUpgradeAsync(TemplateDbContext db)
    {
        var migrator = db.GetService<IMigrator>();
        var previous = db.Database.GetMigrations().TakeWhile(id => !id.EndsWith("_RemoveClusterResource", StringComparison.Ordinal)).Last();
        await migrator.MigrateAsync(previous);
        var user = new User { UserName = "preserved-operator", Email = "operator@example.test", DisplayName = "Preserved operator" };
        db.Users.Add(user);
        var personal = new PersonalApiKey { UserId = user.Id, Name = "Preserved personal key", KeyHash = new string('a', 64), ExpiresAt = DateTime.UtcNow.AddDays(1) };
        db.PersonalApiKeys.Add(personal);
        var role = new IdentityRole("preserved-role");
        db.Roles.Add(role);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        db.RoleClaims.Add(new IdentityRoleClaim<string> { RoleId = role.Id, ClaimType = AppPermissions.Type, ClaimValue = AppPermissionNames.CanRequestMediumRiskWorkflow });
        await db.SaveChangesAsync();
        var group = Guid.NewGuid(); var machine = Guid.NewGuid(); var installation = Guid.NewGuid();
        var key = Guid.NewGuid(); var box = Guid.NewGuid(); var root = Guid.NewGuid();
        var child = Guid.NewGuid(); var queued = Guid.NewGuid(); var enrollment = Guid.NewGuid();
        var request = Guid.NewGuid(); var auditEvent = Guid.NewGuid(); var factId = Guid.NewGuid();
        var at = new DateTime(2026, 11, 15, 12, 30, 0, DateTimeKind.Utc);
        var expires = at.AddYears(1);
        var input = "{\"Service\":\"preserved.service\"}";
        var parameters = JsonSerializer.SerializeToNode(new OperationRequest
        {
            RequestId = request, Workflow = WorkflowNames.Restart, MachineIds = [machine],
            Input = input, Reason = "Preserved approved request"
        })!;
        parameters["ClusterId"] = group;
        var targets = JsonSerializer.Serialize(new[] { machine });
        var metadata = JsonSerializer.SerializeToNode(new WorkflowAuditEntry(WorkflowAuditKind.WorkflowStateChanged,
            at, at, child, root, root, box, WorkflowLocation.Machine, machine, installation,
            WorkflowNames.Restart, WorkflowNames.Version, user.Id, "preserved-reviewer", false, RiskLevel.Medium,
            WorkflowNames.Restart, WorkflowNames.Version, DecisionState.Approved, at, RunState.Queued, RunState.Running))!;
        metadata["ClusterId"] = group;
        var parametersText = parameters.ToJsonString(); var metadataText = metadata.ToJsonString();
        var agentHash = new string('b', 64); var joinHash = new string('c', 64); var receiptHash = new string('d', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Clusters (Id, Name) VALUES ({group}, {"Former resource"})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Machines (Id, ClusterId, Name, KeyHash, AgentVersion, InstallationId, Disabled) VALUES ({machine}, {group}, {"Preserved machine"}, {agentHash}, {"1.0.21"}, {installation}, {false})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO JoinKeys (Id, ClusterId, IssuerId, Name, KeyHash, CreatedAt, ExpiresAt, Revoked) VALUES ({key}, {group}, {user.Id}, {"Preserved join key"}, {joinHash}, {at}, {expires}, {false})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Lockboxes (Id, ClientRequestId, ClusterId, RequesterId, Definition, Version, Risk, Parameters, TargetMachines, Reason, CreatedAt, Decision, ApproverId, Automatic, DecidedAt, DecisionReason, WorkflowId, OperationId) VALUES ({box}, {request}, {group}, {user.Id}, {WorkflowNames.Restart}, {WorkflowNames.Version}, {(int)RiskLevel.Medium}, {parametersText}, {targets}, {"Preserved approved request"}, {at}, {(int)DecisionState.Approved}, {"preserved-reviewer"}, {false}, {at}, {"Approved once"}, {root}, {child})");
        foreach (var (id, state) in new[] { (root, RunState.Running), (child, RunState.Running), (queued, RunState.Queued) })
        {
            Guid? parent = id == root ? null : root;
            Guid? target = id == root ? null : machine;
            var definition = id == root ? WorkflowNames.Lockbox : WorkflowNames.Restart;
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkflowRuns (Id, RootId, ParentId, LockboxId, ClusterId, MachineId, Definition, State, CreatedAt, Result, Input, Version, IsMachineTask, LastSequence) VALUES ({id}, {root}, {parent}, {box}, {group}, {target}, {definition}, {(int)state}, {at}, {"Preserved result"}, {input}, {WorkflowNames.Version}, {id != root}, {41L})");
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentEnrollments (Id, JoinKeyId, LockboxId, InstallationId, MachineName, ReceiptHash, AgentKeyHash, MachineId, CreatedAt) VALUES ({enrollment}, {key}, {box}, {installation}, {"Preserved machine"}, {receiptHash}, {agentHash}, {machine}, {at})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AuditEvents (Id, Time, ActorId, Action, ResourceId, Detail) VALUES ({auditEvent}, {at}, {user.Id}, {"lockbox.approve"}, {box}, {"Original decision"})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkflowAuditOutbox (EventId, Metadata) VALUES ({factId}, {metadataText})");
        var outboxId = await db.WorkflowAuditOutbox.Select(row => row.Id).SingleAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAsync();
        await migrator.MigrateAsync(); // Repeated startup must not mutate IDs or capture new workflow events.
        var persisted = await db.Machines.SingleAsync();
        Assert.AreEqual(machine, persisted.Id); Assert.AreEqual(installation, persisted.InstallationId);
        Assert.AreEqual(agentHash, persisted.KeyHash); Assert.AreEqual("1.0.21", persisted.AgentVersion);
        Assert.AreEqual(personal.KeyHash, (await db.PersonalApiKeys.SingleAsync()).KeyHash);
        Assert.AreEqual(user.Id, (await db.Users.SingleAsync()).Id);
        Assert.AreEqual(role.Id, (await db.Roles.SingleAsync()).Id);
        Assert.AreEqual(role.Id, (await db.UserRoles.SingleAsync()).RoleId);
        Assert.AreEqual(AppPermissionNames.CanRequestMediumRiskWorkflow, (await db.RoleClaims.SingleAsync()).ClaimValue);
        Assert.AreEqual(joinHash, (await db.JoinKeys.SingleAsync()).KeyHash);
        var registration = await db.AgentEnrollments.SingleAsync();
        Assert.AreEqual(enrollment, registration.Id); Assert.AreEqual(machine, registration.MachineId);
        Assert.AreEqual(receiptHash, registration.ReceiptHash); Assert.AreEqual(box, registration.LockboxId);
        var approved = await db.Lockboxes.SingleAsync();
        Assert.AreEqual(box, approved.Id); Assert.AreEqual(root, approved.WorkflowId); Assert.AreEqual(child, approved.OperationId);
        Assert.AreEqual(DecisionState.Approved, approved.Decision); Assert.AreEqual("preserved-reviewer", approved.ApproverId);
        Assert.AreEqual(at, approved.DecidedAt); Assert.AreEqual("Approved once", approved.DecisionReason);
        Assert.AreEqual(targets, approved.TargetMachines);
        var restored = JsonSerializer.Deserialize<OperationRequest>(approved.Parameters)!;
        Assert.AreEqual(request, restored.RequestId); Assert.AreEqual(input, restored.Input);
        Assert.IsFalse(JsonNode.Parse(approved.Parameters)!.AsObject().ContainsKey("ClusterId"));
        var runs = await db.WorkflowRuns.OrderBy(run => run.Id).ToListAsync();
        Assert.HasCount(3, runs); Assert.AreEqual(2, runs.Count(run => run.State == RunState.Running));
        Assert.AreEqual(RunState.Queued, runs.Single(run => run.Id == queued).State);
        Assert.AreEqual(root, runs.Single(run => run.Id == child).ParentId);
        Assert.IsTrue(runs.All(run => run.LastSequence == 41 && run.Result == "Preserved result"));
        Assert.AreEqual(auditEvent, (await db.AuditEvents.SingleAsync()).Id);
        var undelivered = await db.WorkflowAuditOutbox.SingleAsync();
        Assert.AreEqual(outboxId, undelivered.Id); Assert.AreEqual(factId, undelivered.EventId);
        Assert.IsFalse(JsonNode.Parse(undelivered.Metadata)!.AsObject().ContainsKey("ClusterId"));
        metadata.AsObject().Remove("ClusterId");
        Assert.IsTrue(JsonNode.DeepEquals(metadata, JsonSerializer.SerializeToNode(undelivered.Read())));
        if (db.Database.IsSqlite())
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA foreign_keys";
            Assert.AreEqual(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            command.CommandText = "PRAGMA foreign_key_check";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.IsFalse(await reader.ReadAsync());
        }
        Assert.IsFalse(db.Model.GetEntityTypes().Any(entity => entity.ClrType.Name is "Cluster" or "ClusterGrant"));
        Assert.IsFalse(db.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties()).Any(property => property.Name is "ClusterId" or "CanApproveOwnRequests"));
    }
}
