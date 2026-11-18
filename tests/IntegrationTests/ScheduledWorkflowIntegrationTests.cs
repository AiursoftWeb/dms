using System.Net.WebSockets;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class ScheduledWorkflowIntegrationTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    [WorkflowRisk(RiskLevel.Safe), RunCron("0 0 1 1 *")]
    public sealed class OfflineProbeWorkflow : MachineWorkflow
    {
        public override string Name => "test.offline-probe";
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            await context.TraceAsync("Scheduled inspection continues while Controller authentication is unavailable.");
            var status = await context.CallAsync(MachineWorkflows.AgentStatus, new EmptyInput(), token);
            return System.Text.Json.JsonSerializer.Serialize(status);
        }
    }

    [TestMethod]
    public async Task LostControllerConnectionDoesNotStopLocalSchedulingAndReconnectionArchivesIt()
    {
        Server!.Services.GetRequiredService<MachineWorkflowRegistry>().RegisterAttributed(typeof(OfflineProbeWorkflow));
        var setup = await DatabaseAsync(async db =>
        {
            var key = DmsKeyAuthenticationHandler.CreateKey(true);
            var machine = new Machine { Name = "disconnect-probe", KeyHash = DmsKeyAuthenticationHandler.Hash(key) };
            db.Machines.Add(machine); await db.SaveChangesAsync(); return (machine.Id, Key: key);
        });
        var path = Directory.CreateTempSubdirectory("dms-cron-disconnect-").FullName;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var journal = new TaskJournal(path);
        var registry = new MachineWorkflowRegistry(); registry.RegisterAttributed(typeof(OfflineProbeWorkflow));
        var agent = new AgentClient(Http.BaseAddress!, setup.Key, journal, registry);
        var execution = agent.RunAsync(stop.Token);
        try
        {
            while (await DatabaseAsync(db => db.Machines.Where(m => m.Id == setup.Id).Select(m => m.InstallationId).SingleAsync()) == null)
                await Task.Delay(20, stop.Token);
            await DatabaseAsync(async db => { (await db.Machines.FindAsync(setup.Id))!.Disabled = true; await db.SaveChangesAsync(); return true; });
            Server.Services.GetRequiredService<AgentSessions>().Get(setup.Id)!.Abort();
            while (Server.Services.GetRequiredService<AgentSessions>().Get(setup.Id) != null) await Task.Delay(20, stop.Token);
            agent.TickSchedules(new DateTime(DateTime.UtcNow.Year - 1, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            while (!journal.Snapshot().Any(t => t.Definition == "test.offline-probe" && t.State == RunState.Succeeded))
                await Task.Delay(20, stop.Token);
            var task = journal.Snapshot().Single(t => t.Definition == "test.offline-probe");
            Assert.IsFalse(await DatabaseAsync(db => db.WorkflowRuns.AnyAsync(r => r.Id == task.Id)));
            Assert.IsTrue(task.Pending.Any(e => e.ParentId == task.Id && e.State == RunState.Succeeded));
            await DatabaseAsync(async db => { (await db.Machines.FindAsync(setup.Id))!.Disabled = false; await db.SaveChangesAsync(); return true; });
            while (!await DatabaseAsync(db => db.WorkflowRuns.AnyAsync(r => r.Id == task.Id && r.State == RunState.Succeeded)))
                await Task.Delay(20, stop.Token);
            var restored = await DatabaseAsync(db => db.WorkflowRuns.Where(r => r.RootId == task.Id).AsNoTracking().ToListAsync());
            Assert.HasCount(2, restored);
            Assert.IsTrue(restored.All(r => r.Origin == WorkflowOrigin.Automatic && r.LockboxId == null));
            Assert.AreEqual(WorkflowTrigger.Cron, restored.Single(r => r.Id == task.Id).Trigger);
        }
        finally
        {
            await stop.CancelAsync(); try { await execution; } catch (OperationCanceledException) { }
            journal.Dispose(); Directory.Delete(path, true);
        }
    }

    [TestMethod]
    public async Task TwoAgentsReplayOfflineScheduledTreesWithoutLockboxesOrDuplicateAuditEvents()
    {
        var setups = await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var machines = Enumerable.Range(0, 2).Select(i =>
            {
                var key = DmsKeyAuthenticationHandler.CreateKey(true);
                var machine = new Machine { Name = "scheduled-" + i, KeyHash = DmsKeyAuthenticationHandler.Hash(key) };
                db.Machines.Add(machine); return (Machine: machine.Id, Key: key);
            }).ToArray();
            await db.SaveChangesAsync(); return machines;
        });
        var paths = setups.Select(_ => Directory.CreateTempSubdirectory("dms-scheduled-agent-").FullName).ToArray();
        var journals = new List<TaskJournal>();
        var executions = new List<Task>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var roots = new List<Guid>();
        var originalEvents = new List<WorkflowEvent[]>();
        try
        {
            foreach (var (setup, path) in setups.Zip(paths))
            {
                // Exercise a disconnected Agent, then a real journal reopen before its first connection.
                using (var offline = new TaskJournal(path))
                {
                    new AgentClient(Http.BaseAddress!, setup.Key, offline).TickSchedules(DateTime.UtcNow.AddMinutes(-10), startup: true);
                    while (offline.Snapshot().Any(t => !t.State.Terminal())) await Task.Delay(10, stop.Token);
                    roots.Add(offline.Snapshot().Single().Id);
                    originalEvents.Add(offline.Snapshot().Single().Pending.ToArray());
                }
                var recovered = new TaskJournal(path); journals.Add(recovered);
                executions.Add(new AgentClient(Http.BaseAddress!, setup.Key, recovered).RunAsync(stop.Token));
            }
            while (await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => roots.Contains(r.Id) && r.State == RunState.Succeeded)) != 2)
                await Task.Delay(30, stop.Token);
            while (journals.Any(j => j.Snapshot().Any(t => t.Pending.Count != 0))) await Task.Delay(30, stop.Token);
            var runs = await DatabaseAsync(db => db.WorkflowRuns.Where(r => roots.Contains(r.RootId)).AsNoTracking().ToListAsync());
            Assert.HasCount(4, runs);
            Assert.IsTrue(runs.All(r => r.Origin == WorkflowOrigin.Automatic && r.LockboxId == null && r.State == RunState.Succeeded));
            Assert.HasCount(2, runs.Select(r => r.MachineId).Distinct());
            foreach (var root in roots)
            {
                var parent = runs.Single(r => r.Id == root);
                Assert.AreEqual(root, parent.RootId); Assert.IsNull(parent.ParentId);
                Assert.AreEqual(WorkflowTrigger.AgentStartup, parent.Trigger);
                var child = runs.Single(r => r.ParentId == root);
                Assert.AreEqual(WorkflowTrigger.Subworkflow, child.Trigger);
                Assert.AreEqual(parent.ScheduleId, child.ScheduleId);
                Assert.AreEqual(parent.ScheduledAt, child.ScheduledAt);
            }
            var audit = (await DatabaseAsync(db => db.WorkflowAuditOutbox.AsNoTracking().ToListAsync()))
                .Select(e => e.Read()).Where(e => e.RootWorkflowId != null && roots.Contains(e.RootWorkflowId.Value)).ToList();
            Assert.HasCount(4, audit.Where(e => e.Kind == WorkflowAuditKind.WorkflowCreated));
            Assert.IsTrue(audit.All(e => e.Origin == WorkflowOrigin.Automatic && e.LockboxId == null && e.RequesterId == null && e.ApproverId == null && e.AutomaticApproval == null));
            Assert.IsFalse(audit.Any(e => e.Kind is WorkflowAuditKind.LockboxSubmitted or WorkflowAuditKind.LockboxApproved));
            await LoginAsAdmin();
            foreach (var root in roots)
            {
                using var response = await Http.GetAsync("/Dms/Operation?id=" + root);
                response.EnsureSuccessStatusCode();
                StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Automatic");
            }
            // Replay a previously archived registration and its full original event sequence.
            var setup0 = setups[0];
            using var scope = Server!.Services.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService<AgentEvents>();
            using var session = new AgentSession(new ClientWebSocket(), DmsKeyAuthenticationHandler.Hash(setup0.Key)) { Ready = true };
            var rootTask = journals[0].Snapshot().Single(t => t.Id == roots[0]);
            var registration = new AgentMessage { Kind = "scheduled", TaskId = rootTask.Id,
                Definition = rootTask.Definition, Version = rootTask.Version, Schedule = rootTask.Schedule };
            var priorAuditCount = await DatabaseAsync(db => db.WorkflowAuditOutbox.CountAsync());
            await handler.HandleAsync(setup0.Machine, session, registration);
            foreach (var item in originalEvents[0])
                await handler.HandleAsync(setup0.Machine, session, new AgentMessage { Kind = "event", TaskId = rootTask.Id, Event = item });
            var after = await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => roots.Contains(r.RootId)));
            Assert.AreEqual(runs.Count, after);
            Assert.AreEqual(priorAuditCount, await DatabaseAsync(db => db.WorkflowAuditOutbox.CountAsync()));
            registration.Schedule = registration.Schedule! with { Expression = "* * * * *" };
            Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => handler.HandleAsync(setup0.Machine, session, registration))).StatusCode);
        }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(executions); } catch (OperationCanceledException) { }
            foreach (var journal in journals) journal.Dispose();
            foreach (var path in paths) Directory.Delete(path, true);
        }
    }
}
