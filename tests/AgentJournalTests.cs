using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentJournalTests
{
    [TestMethod]
    public async Task AcknowledgingOneCompletedTaskKeepsTheOtherTaskReadyToReplayAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory("dms-journal-replay-");
        var acknowledged = Guid.NewGuid();
        var awaitingReceipt = Guid.NewGuid();
        try
        {
            using (var journal = new TaskJournal(directory.FullName))
            {
                foreach (var id in new[] { acknowledged, awaitingReceipt })
                {
                    journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Performance });
                    Assert.IsNotNull(journal.Start(id));
                    await journal.ReportAsync(id, new WorkflowEvent { WorkflowId = id, State = RunState.Succeeded });
                }

                journal.Acknowledge(acknowledged, 1);
            }

            using var recovered = new TaskJournal(directory.FullName);
            var pendingTask = recovered.Snapshot(activeOnly: true).Single();
            Assert.AreEqual(awaitingReceipt, pendingTask.Id);
            var pending = pendingTask.Pending.Single();
            Assert.AreEqual(awaitingReceipt, pending.WorkflowId);
            Assert.AreEqual(RunState.Succeeded, pending.State);
            Assert.AreEqual(1L, pending.Sequence);
            Assert.IsNull(recovered.Start(awaitingReceipt));

            recovered.Acknowledge(awaitingReceipt, pending.Sequence);
            Assert.IsEmpty(recovered.Snapshot(activeOnly: true));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class WaitingWorkflow : MachineWorkflow
    {
        public override string Name => WorkflowNames.Performance;
        public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        }
    }

    private sealed class ParentWorkflow : MachineWorkflow
    {
        public override string Name => WorkflowNames.Restart;
        public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) => context.CallAsync(new WaitingWorkflow(), token);
    }

    [TestMethod]
    public async Task CancellationCanTargetAnIndividualSubworkflow()
    {
        var root = Guid.NewGuid();
        var events = new List<WorkflowEvent>();
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new MachineWorkflowContext(root, null, item =>
        {
            events.Add(item);
            if (item.ParentId == root && item.State == RunState.Running) started.TrySetResult(item.WorkflowId);
            return Task.CompletedTask;
        });
        var execution = context.RunAsync(new ParentWorkflow(), CancellationToken.None);
        var child = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.CancelAsync(child);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(events.Any(e => e.WorkflowId == child && e.State == RunState.Cancelled));
        Assert.IsTrue(events.Any(e => e.WorkflowId == root && e.State == RunState.Failed));
    }

    [TestMethod]
    public void StartedTaskBecomesUncertainAndCannotRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "dms-journal-" + Guid.NewGuid());
        var id = Guid.NewGuid();
        Guid installation;
        try
        {
            using (var journal = new TaskJournal(path))
            {
                installation = journal.InstallationId;
                journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Restart, Input = "{\"Service\":\"test.service\"}" });
                Assert.IsNotNull(journal.Start(id));
                Assert.IsNull(journal.Start(id));
            }
            using var recovered = new TaskJournal(path);
            Assert.AreEqual(installation, recovered.InstallationId);
            Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single().State);
            Assert.IsNull(recovered.Start(id));
            recovered.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Restart, Input = "{\"Service\":\"test.service\"}" });
            Assert.IsNull(recovered.Start(id));
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow("machine.ssh.disable", false)]
    [DataRow("machine.system.execute-script", false)]
    [DataRow(WorkflowNames.AgentUpgrade, true)]
    [DataRow("machine.system.reboot", true)]
    [DataRow("machine.system.poweroff", true)]
    public void UpgradeCanPreserveHistoricalWorkButWaitsForLiveWorkAndLifecycleVerification(string definition, bool lifecycle)
    {
        var directory = Directory.CreateTempSubdirectory("dms-upgrade-journal-");
        var interrupted = Guid.NewGuid();
        var upgrade = Guid.NewGuid();
        try
        {
            using (var journal = new TaskJournal(directory.FullName))
            {
                journal.Accept(new AgentMessage { TaskId = interrupted, Definition = definition });
                Assert.IsNotNull(journal.Start(interrupted));
                Assert.IsTrue(journal.BlocksAgentUpgrade(upgrade));
            }
            using var recovered = new TaskJournal(directory.FullName);
            var task = recovered.Snapshot().Single();
            Assert.AreEqual(RunState.NeedsVerification, task.State);
            Assert.AreEqual(lifecycle, recovered.BlocksAgentUpgrade(upgrade));
            Assert.AreEqual(RunState.NeedsVerification, task.Pending.Single().State);
            Assert.IsNull(recovered.Start(interrupted));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public async Task AcceptedTaskCanResumeAndAcknowledgedEventsStayAcknowledged()
    {
        var path = Path.Combine(Path.GetTempPath(), "dms-journal-" + Guid.NewGuid());
        var id = Guid.NewGuid();
        try
        {
            using (var journal = new TaskJournal(path)) journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Performance });
            using (var journal = new TaskJournal(path))
            {
                Assert.IsNotNull(journal.Start(id));
                await journal.ReportAsync(id, new WorkflowEvent { WorkflowId = id, State = RunState.Succeeded });
                Assert.HasCount(1, journal.Snapshot().Single().Pending);
                journal.Acknowledge(id, 1);
            }
            using var recovered = new TaskJournal(path);
            Assert.AreEqual(RunState.Succeeded, recovered.Snapshot().Single().State);
            Assert.IsEmpty(recovered.Snapshot().Single().Pending);
            Assert.IsNull(recovered.Start(id));
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnsupportedOfferFailureSurvivesRestartAndIsNotReplayed(bool accepted)
    {
        var path = Path.Combine(Path.GetTempPath(), "dms-rejected-offer-" + Guid.NewGuid());
        var offer = new AgentMessage { TaskId = Guid.NewGuid(), Definition = "machine.future", Version = "next" };
        try
        {
            using (var journal = new TaskJournal(path))
            {
                if (accepted) journal.Accept(offer);
                Assert.IsTrue(journal.RejectBeforeStart(offer, "Upgrade the Agent."));
                Assert.IsFalse(journal.RejectBeforeStart(offer, "Duplicate offer."));
                Assert.IsNull(journal.Start(offer.TaskId));
                var task = journal.Snapshot().Single();
                Assert.AreEqual(RunState.Failed, task.State);
                Assert.HasCount(1, task.Pending);
                Assert.AreEqual("next", task.Pending.Single().Version);
                StringAssert.Contains(task.Pending.Single().Result, "UnsupportedWorkflow");
                Func<AgentMessage, string, bool> reject = journal.RejectBeforeStart;
                Assert.Throws<InvalidDataException>(() => reject(
                    new AgentMessage { TaskId = offer.TaskId, Definition = offer.Definition, Version = "changed" }, "Changed offer."));
            }
            using (var recovered = new TaskJournal(path))
            {
                Assert.AreEqual(RunState.Failed, recovered.Snapshot().Single().State);
                Assert.HasCount(1, recovered.Snapshot().Single().Pending);
                recovered.Acknowledge(offer.TaskId, 1);
            }
            using var acknowledged = new TaskJournal(path);
            Assert.IsFalse(acknowledged.RejectBeforeStart(offer, "Do not recreate an acknowledged failure."));
            Assert.IsEmpty(acknowledged.Snapshot().Single().Pending);
            Assert.IsNull(acknowledged.Start(offer.TaskId));
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow(RunState.Running)]
    [DataRow(RunState.NeedsVerification)]
    [DataRow(RunState.Succeeded)]
    [DataRow(RunState.Cancelled)]
    public async Task OfferRejectionDoesNotReplaceAnExistingExecutionOutcome(RunState state)
    {
        var path = Path.Combine(Path.GetTempPath(), "dms-rejected-existing-" + Guid.NewGuid());
        var offer = new AgentMessage { TaskId = Guid.NewGuid(), Definition = WorkflowNames.AgentStatus };
        try
        {
            using var journal = new TaskJournal(path);
            journal.Accept(offer);
            Assert.IsNotNull(journal.Start(offer.TaskId));
            await journal.ReportAsync(offer.TaskId, new WorkflowEvent { WorkflowId = offer.TaskId, State = state });
            Assert.IsFalse(journal.RejectBeforeStart(offer, "Definition is no longer installed."));
            Assert.AreEqual(state, journal.Snapshot().Single().State);
            Assert.HasCount(1, journal.Snapshot().Single().Pending);
            Assert.IsNull(journal.Start(offer.TaskId));
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    public async Task ProcessWorkflowInjectsLockboxContextAndJournalPersistsLockbox()
    {
        var boxId = new LockboxId(Guid.NewGuid());
        var triggeredAt = new DateTime(2026, 11, 28, 12, 0, 0, DateTimeKind.Utc);
        var lockbox = new LockboxContext(boxId, "emp-123", "Alice (@alice)", "Emergency restart", RiskLevel.High, triggeredAt);
        var path = Path.Combine(Path.GetTempPath(), "dms-lockbox-journal-" + Guid.NewGuid());
        try
        {
            var taskId = Guid.NewGuid();
            using (var journal = new TaskJournal(path))
            {
                journal.Accept(new AgentMessage
                {
                    Kind = "offer",
                    TaskId = taskId,
                    Definition = WorkflowNames.AgentStatus,
                    Lockbox = lockbox
                });
            }
            using (var reopened = new TaskJournal(path))
            {
                var recovered = reopened.Snapshot().Single();
                Assert.AreEqual(lockbox, recovered.Lockbox);
            }
        }
        finally { Directory.Delete(path, true); }

        var context = new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask, lockbox: lockbox);
        var result = await context.RunAsync(
            new ProcessWorkflow("/bin/sh", ["-c", "printf \"%s|%s|%s|%s|%s|%s\" \"$DMS_LOCKBOX_ID\" \"$DMS_REQUESTER_ID\" \"$DMS_REQUESTER_NAME\" \"$DMS_LOCKBOX_REASON\" \"$DMS_LOCKBOX_RISK\" \"$DMS_TRIGGERED_AT\""], captureOutput: true),
            CancellationToken.None);
        var output = JsonSerializer.Deserialize<ProcessResult>(result)!.Output;
        Assert.AreEqual($"{boxId}|emp-123|Alice (@alice)|Emergency restart|High|{triggeredAt:O}", output);
    }

    [TestMethod]
    public void AcceptedCancellationPreventsStart()
    {
        var path = Path.Combine(Path.GetTempPath(), "dms-journal-" + Guid.NewGuid());
        try
        {
            using var journal = new TaskJournal(path);
            var id = Guid.NewGuid();
            journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Performance });
            Assert.IsTrue(journal.CancelBeforeStart(id));
            Assert.IsNull(journal.Start(id));
            Assert.AreEqual(RunState.Cancelled, journal.Snapshot().Single().State);
        }
        finally { Directory.Delete(path, true); }
    }

    [TestMethod]
    [DataRow(9000, "x", "output exceeds")]
    [DataRow(4000, "中", "serialized output limit")]
    public async Task CapturedOutputIsAResultAndOversizedQueriesFailWithoutTruncation(int count, string character, string error)
    {
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        var result = await context.RunAsync(new ProcessWorkflow("/usr/bin/printf", ["fixed output"], captureOutput: true), CancellationToken.None);
        Assert.AreEqual("fixed output", JsonSerializer.Deserialize<ProcessResult>(result)!.Output);
        Assert.IsTrue(events.Any(e => e.IsTrace && e.Message == "fixed output"));
        events.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync(
            new ProcessWorkflow("/usr/bin/printf", ["%s", string.Concat(Enumerable.Repeat(character, count))], captureOutput: true), CancellationToken.None));
        var failure = events.Single(e => e.State == RunState.Failed);
        StringAssert.Contains(failure.Result, error);
        Assert.IsFalse(events.Any(e => e.State == RunState.Succeeded));
    }

    [TestMethod]
    [DataRow("*.service")]
    [DataRow("--all")]
    [DataRow("ssh.service; reboot")]
    public async Task ServiceStatusRejectsExpressionsBeforeStartingAProcess(string service)
    {
        var events = new List<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, item => { events.Add(item); return Task.CompletedTask; });
        await Assert.ThrowsAsync<ArgumentException>(() => context.RunAsync(new ServiceStatusWorkflow(service), CancellationToken.None));
        Assert.IsFalse(events.Any(e => e.Definition == WorkflowNames.Process));
    }

    [TestMethod]
    [DataRow("ssh.service", true)]
    [DataRow("test@one.service", true)]
    [DataRow("ssh.service; reboot", false)]
    [DataRow("--all", false)]
    [DataRow("*.service", false)]
    [DataRow("/tmp/a.service", false)]
    [DataRow("ssh.service\n", false)]
    public void ServiceNamesCannotInjectCommandsOrWildcards(string value, bool expected) =>
        Assert.AreEqual(expected, RestartServiceWorkflow.ValidService(value));
}
