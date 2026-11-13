using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentJournalTests
{
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
                journal.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Restart, Service = "test.service" });
                Assert.IsNotNull(journal.Start(id));
                Assert.IsNull(journal.Start(id));
            }
            using var recovered = new TaskJournal(path);
            Assert.AreEqual(installation, recovered.InstallationId);
            Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single().State);
            Assert.IsNull(recovered.Start(id));
            recovered.Accept(new AgentMessage { TaskId = id, Definition = WorkflowNames.Restart, Service = "test.service" });
            Assert.IsNull(recovered.Start(id));
        }
        finally { Directory.Delete(path, true); }
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
