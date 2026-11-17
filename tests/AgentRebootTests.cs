using Aiursoft.DMS.Workflows.Operations;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentRebootTests
{
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task RestartConfirmsOnlyANewBootOnTheOriginalInstallationBeforeDeadline(bool newBoot, bool beforeDeadline)
    {
        var directory = Directory.CreateTempSubdirectory("dms-reboot-recovery-").FullName;
        try
        {
            var state = Path.Combine(directory, "state"); var root = Path.Combine(directory, "root");
            Directory.CreateDirectory(Path.Combine(root, "proc/sys/kernel/random"));
            var previous = Guid.NewGuid().ToString(); var taskId = Guid.NewGuid(); Guid installation;
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
            {
                installation = journal.InstallationId;
                new AgentConfiguration("http://controller.invalid", Guid.NewGuid(), installation, "fixture", "", "dmsa_fixture", Guid.NewGuid()).Save(state);
                journal.Accept(new AgentMessage { TaskId = taskId, Definition = OperationsWorkflows.SystemReboot.Name, Version = OperationsWorkflows.SystemReboot.Version, Input = OperationsWorkflows.SystemReboot.Serialize(new RebootInput()) });
                journal.Start(taskId);
                var handoff = new RebootHandoff(taskId, taskId, null, installation, previous, DateTime.UtcNow.AddMinutes(beforeDeadline ? 5 : -5), "scheduled");
                await File.WriteAllTextAsync(Path.Combine(state, "reboot.json"), JsonSerializer.Serialize(handoff));
            }
            await File.WriteAllTextAsync(Path.Combine(root, "proc/sys/kernel/random/boot_id"), newBoot ? Guid.NewGuid().ToString() : previous);
            var nativeCalls = 0;
            using var recovered = new TaskJournal(Path.Combine(state, "journal")); Assert.AreEqual(RunState.NeedsVerification, recovered.Snapshot().Single().State);
            await new AgentReboot(state, recovered, new OperationHost(root, (_, _) => { nativeCalls++; throw new InvalidOperationException("Recovery must not execute a reboot."); })).ConfirmAsync();
            var task = recovered.Snapshot().Single();
            Assert.AreEqual(newBoot && beforeDeadline ? RunState.Succeeded : RunState.NeedsVerification, task.State);
            Assert.AreEqual(taskId, task.Id); Assert.AreEqual(installation, recovered.InstallationId); Assert.AreEqual(0, nativeCalls);
        }
        finally { Directory.Delete(directory, true); }
    }
    [TestMethod]
    public async Task CancellingAfterSchedulingRetainsAnUncertainHandoffAndDoesNotScheduleTwice()
    {
        var directory = Directory.CreateTempSubdirectory("dms-reboot-cancel-").FullName;
        TaskJournal? journal = null; CancellationTokenSource? stop = null;
        try
        {
            var state = Path.Combine(directory, "state"); var root = Path.Combine(directory, "root");
            Directory.CreateDirectory(Path.Combine(root, "proc/sys/kernel/random"));
            await File.WriteAllTextAsync(Path.Combine(root, "proc/sys/kernel/random/boot_id"), Guid.NewGuid().ToString());
            journal = new TaskJournal(Path.Combine(state, "journal")); var taskId = Guid.NewGuid();
            new AgentConfiguration("http://controller.invalid", Guid.NewGuid(), journal.InstallationId, "fixture", "", "dmsa_fixture", Guid.NewGuid()).Save(state);
            journal.Accept(new AgentMessage { TaskId = taskId, Definition = OperationsWorkflows.SystemReboot.Name, Version = "1", Input = OperationsWorkflows.SystemReboot.Serialize(new RebootInput()) }); journal.Start(taskId);
            stop = new CancellationTokenSource(); var commands = new List<NativeCommand>();
            Action cancel = stop.Cancel;
            var host = new OperationHost(root, (command, _) => { commands.Add(command); cancel(); return Task.FromResult(new NativeResult(0, "", "")); });
            var context = new MachineWorkflowContext(taskId, null, _ => Task.CompletedTask);
            await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => new AgentReboot(state, journal, host).PrepareAsync(taskId, context, new RebootInput(), stop.Token));
            Assert.HasCount(1, commands); Assert.AreEqual("systemd-run", commands[0].Executable);
            Assert.IsTrue(commands[0].Arguments.Contains("/usr/bin/systemctl")); Assert.IsTrue(commands[0].Arguments.Contains("reboot"));
            var handoff = JsonSerializer.Deserialize<RebootHandoff>(await File.ReadAllTextAsync(Path.Combine(state, "reboot.json")))!;
            Assert.AreEqual("uncertain", handoff.Phase); Assert.AreEqual(taskId, handoff.WorkflowId);
        }
        finally { stop?.Dispose(); journal?.Dispose(); Directory.Delete(directory, true); }
    }
}
