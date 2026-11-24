using System.Reflection;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class ScheduledWorkflowTests
{
    private static readonly DateTime Slot = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static ScheduledInvocation Occurrence(DateTime time) => new("test.schedule", "*/5 * * * *", time, WorkflowTrigger.Cron);

    [TestMethod]
    public void SharedDefinitionsHaveOneRiskDeclarationAndSchedulesUseUtcFiveFieldCron()
    {
        foreach (var owner in new[] { typeof(MachineWorkflows), typeof(ClusterWorkflows), typeof(OperationsWorkflows) })
        foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetValue(null) is WorkflowDefinition definition)
                Assert.AreEqual(WorkflowMetadata.Risk(field), definition.Risk, field.Name);
        var registry = new MachineWorkflowRegistry();
        var schedule = registry.Schedules.Single(s => s.Id == "machine.agent.scheduled-status");
        Assert.IsTrue(schedule.Matches(Slot));
        Assert.IsFalse(schedule.Matches(Slot.AddMinutes(1)));
        Assert.IsTrue(schedule.RunOnStartup);
        Assert.AreEqual(RiskLevel.Safe, schedule.Definition.Risk);
        Assert.IsFalse(schedule.ReconcileAfterInterruption);
        var lease = registry.Schedules.Single(s => s.Id == "machine.ssh.reconcile-lease");
        Assert.IsTrue(lease.RunOnStartup && lease.ReconcileAfterInterruption);
        Assert.AreEqual(RiskLevel.Medium, lease.Definition.Risk);
        var upgrade = registry.Schedules.Single(s => s.Id == ScheduledAgentUpgradeWorkflow.DefinitionName);
        Assert.IsTrue(upgrade.Matches(Slot.Date) && upgrade.Matches(Slot.Date.AddHours(8)) && upgrade.Matches(Slot.Date.AddHours(16)));
        Assert.IsFalse(upgrade.Matches(Slot) || upgrade.RunOnStartup || upgrade.ReconcileAfterInterruption);
        Assert.AreEqual(RiskLevel.High, upgrade.Definition.Risk);
        Assert.IsFalse(upgrade.Definition.PublicEntry);
        Assert.Throws<InvalidOperationException>(() => registry.RegisterAttributed(typeof(MissingRiskWorkflow)));
        Assert.Throws<Cronos.CronFormatException>(() => registry.RegisterAttributed(typeof(InvalidCronWorkflow)));
        Assert.Throws<InvalidOperationException>(() => registry.RegisterAttributed(typeof(InputWorkflow)));
        Assert.Throws<ArgumentException>(() => registry.RegisterAttributed(typeof(ScheduledAgentStatusWorkflow)));
    }

    [TestMethod]
    public async Task DurableSlotsPreventOverlapDuplicatesAndClockRollbackWithoutReplayingDowntime()
    {
        var path = Directory.CreateTempSubdirectory("dms-cron-").FullName;
        try
        {
            using (var journal = new TaskJournal(path))
            {
                var first = journal.AcceptScheduled("test.schedule", "1", Occurrence(Slot))!;
                journal.Start(first.Id);
                Assert.IsNull(journal.AcceptScheduled("test.schedule", "1", Occurrence(Slot.AddMinutes(5))));
                await journal.ReportAsync(first.Id, new WorkflowEvent { WorkflowId = first.Id, State = RunState.Succeeded });
                journal.Acknowledge(first.Id, 1);
            }
            using var recovered = new TaskJournal(path);
            Assert.IsNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot)));
            Assert.IsNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot.AddMinutes(-5))));
            Assert.IsNotNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot.AddDays(1))));
            Assert.HasCount(2, recovered.Snapshot());
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    public void RestartKeepsAcceptedWorkButBlocksUncertainStartedPlan()
    {
        var path = Directory.CreateTempSubdirectory("dms-cron-recovery-").FullName;
        Guid accepted, uncertain;
        try
        {
            using (var journal = new TaskJournal(path))
            {
                accepted = journal.AcceptScheduled("accepted", "1", Occurrence(Slot) with { ScheduleId = "accepted" })!.Id;
                uncertain = journal.AcceptScheduled("test.schedule", "1", Occurrence(Slot))!.Id;
                journal.Start(uncertain);
            }
            using var recovered = new TaskJournal(path);
            Assert.IsNotNull(recovered.Start(accepted));
            Assert.IsNull(recovered.Start(uncertain));
            Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single(t => t.Id == uncertain).State);
            Assert.IsNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot.AddMinutes(5))));
            Assert.IsNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot), reconcileAfterInterruption: true));
            Assert.IsNotNull(recovered.AcceptScheduled("test.schedule", "1", Occurrence(Slot.AddMinutes(5)), reconcileAfterInterruption: true));
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task OfflineCronUsesNormalChildRunnerAndPreservesCaughtFailureInItsTree()
    {
        var path = Directory.CreateTempSubdirectory("dms-cron-offline-").FullName;
        try
        {
            using var journal = new TaskJournal(path);
            var registry = new MachineWorkflowRegistry();
            registry.RegisterAttributed(typeof(CatchingWorkflow), typeof(FailingWorkflow));
            var agent = new AgentClient(new Uri("http://127.0.0.1:1"), "unused", journal, registry);
            agent.TickSchedules(Slot);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (journal.Snapshot().Any(t => !t.State.Terminal())) await Task.Delay(10, timeout.Token);
            var caught = journal.Snapshot().Single(t => t.Definition == "test.catching");
            Assert.AreEqual(RunState.Warning, caught.State);
            Assert.IsTrue(caught.Pending.Any(e => e.ParentId == caught.Id && e.State == RunState.Failed));
            Assert.IsTrue(caught.Pending.Any(e => e.IsTrace && e.Message.Contains("Caught child", StringComparison.Ordinal)));
            var failed = journal.Snapshot().Single(t => t.Definition == "test.failing");
            Assert.AreEqual(RunState.Failed, failed.State);
            Assert.AreEqual(RunState.Succeeded, journal.Snapshot().Single(t => t.Definition == "machine.agent.scheduled-status").State);
            agent.TickSchedules(Slot); Assert.HasCount(4, journal.Snapshot());
            agent.TickSchedules(Slot.AddMinutes(1)); Assert.HasCount(4, journal.Snapshot());
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task StartupAndClockGapsCatchUpOnlyTheLatestAgentUpgradeSlot()
    {
        var path = Directory.CreateTempSubdirectory("dms-upgrade-catchup-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var controller = new Uri("http://127.0.0.1:1");
            var previousSlot = Slot.Date.AddHours(8);
            using (var journal = new TaskJournal(path.FullName))
            {
                var agent = new AgentClient(controller, "unused", journal);
                agent.TickSchedules(Slot, startup: true);
                while (journal.Snapshot().Any(task => !task.State.Terminal())) await Task.Delay(10, timeout.Token);
                var occurrence = journal.Snapshot().Single(task => task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName).Schedule!;
                Assert.AreEqual(previousSlot, occurrence.ScheduledAt);
                Assert.AreEqual(WorkflowTrigger.Cron, occurrence.Trigger);
            }

            using var recovered = new TaskJournal(path.FullName);
            var restarted = new AgentClient(controller, "unused", recovered);
            restarted.TickSchedules(Slot.AddMinutes(3), startup: true);
            Assert.HasCount(1, recovered.Snapshot().Where(task => task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName));
            restarted.TickSchedules(Slot.AddDays(1).AddHours(6));
            while (recovered.Snapshot().Any(task => !task.State.Terminal())) await Task.Delay(10, timeout.Token);
            var upgrades = recovered.Snapshot().Where(task => task.Definition == ScheduledAgentUpgradeWorkflow.DefinitionName).ToArray();
            CollectionAssert.AreEquivalent(new[] { previousSlot, Slot.Date.AddDays(1).AddHours(16) },
                upgrades.Select(task => task.Schedule!.ScheduledAt).ToArray());
        }
        finally { path.Delete(recursive: true); }
    }

    [TestMethod]
    public async Task AgentShutdownCancelsAndDrainsRunningPlansWhileOverlappingSlotsAreSkipped()
    {
        var path = Directory.CreateTempSubdirectory("dms-cron-shutdown-").FullName;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var journal = new TaskJournal(path);
            var registry = new MachineWorkflowRegistry(); registry.RegisterAttributed(typeof(WaitingWorkflow));
            var agent = new AgentClient(new Uri("http://127.0.0.1:1"), "unused", journal, registry);
            var execution = agent.RunAsync(stop.Token);
            try
            {
                while (!journal.Snapshot().Any(t => t.Definition == "test.waiting" && t.State == RunState.Running))
                    await Task.Delay(10, stop.Token);
                var now = DateTime.UtcNow;
                var nextSlot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, DateTimeKind.Utc).AddMinutes(5);
                agent.TickSchedules(nextSlot);
                Assert.HasCount(1, journal.Snapshot().Where(t => t.Definition == "test.waiting"));
            }
            finally
            {
                await stop.CancelAsync();
                try { await execution; } catch (OperationCanceledException) { }
            }
            Assert.AreEqual(RunState.Cancelled, journal.Snapshot().Single(t => t.Definition == "test.waiting").State);
        }
        finally { Directory.Delete(path, true); }
    }

    [WorkflowRisk(RiskLevel.Safe), RunCron("*/5 * * * *", RunOnStartup = true)]
    public sealed class WaitingWorkflow : MachineWorkflow
    {
        public override string Name => "test.waiting";
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            await context.TraceAsync("Waiting for cancellation.");
            await Task.Delay(Timeout.Infinite, token);
            return "{}";
        }
    }

    public sealed class MissingRiskWorkflow : MachineWorkflow
    {
        public override string Name => "invalid.no-risk";
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => Task.FromResult("{}");
    }
    [WorkflowRisk(RiskLevel.Safe), RunCron("*/5 * * a")]
    public sealed class InvalidCronWorkflow : MachineWorkflow
    {
        public override string Name => "invalid.cron";
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => Task.FromResult("{}");
    }
    [WorkflowRisk(RiskLevel.Safe), RunCron("*/5 * * * *")]
    public sealed class InputWorkflow : MachineWorkflow
    {
        public override string Name => "invalid.input";
        public override string Input => "{\"Required\":true}";
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => Task.FromResult("{}");
    }
    [WorkflowRisk(RiskLevel.Safe), RunCron("*/5 * * * *")]
    public sealed class FailingWorkflow : MachineWorkflow
    {
        public override string Name => "test.failing";
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => throw new InvalidOperationException("Expected example failure.");
    }
    [WorkflowRisk(RiskLevel.Safe), RunCron("*/5 * * * *")]
    public sealed class CatchingWorkflow : MachineWorkflow
    {
        public override string Name => "test.catching";
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            try { await context.CallAsync(new FailingWorkflow(), token); }
            catch (ChildWorkflowException failure) { await context.TraceAsync($"Caught child {failure.WorkflowId}."); }
            await context.WarningAsync("The expected child failure was handled.");
            return JsonSerializer.Serialize(new { Handled = true });
        }
    }
}
