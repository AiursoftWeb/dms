using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class AgentPoweroffTests
{
    [TestMethod]
    public void PoweroffInputValidatesRange()
    {
        Assert.Throws<ArgumentException>(() => new PoweroffInput(4).Validate());
        Assert.Throws<ArgumentException>(() => new PoweroffInput(3601).Validate());
        new PoweroffInput(5).Validate();
        new PoweroffInput().Validate();
        new PoweroffInput(3600).Validate();
    }

    [TestMethod]
    public async Task PoweroffSchedulesSystemdRunAndReturnsSummary()
    {
        var directory = Directory.CreateTempSubdirectory("dms-poweroff-test-").FullName;
        TaskJournal? journal = null;
        try
        {
            var state = Path.Combine(directory, "state");
            var root = Path.Combine(directory, "root");
            Directory.CreateDirectory(state);
            journal = new TaskJournal(Path.Combine(state, "journal"));
            var taskId = Guid.NewGuid();
            new AgentConfiguration("http://controller.invalid", Guid.NewGuid(), journal.InstallationId, "fixture", "", "dmsa_fixture", Guid.NewGuid()).Save(state);
            journal.Accept(new AgentMessage
            {
                TaskId = taskId,
                Definition = OperationsWorkflows.SystemPoweroff.Name,
                Version = OperationsWorkflows.SystemPoweroff.Version,
                Input = OperationsWorkflows.SystemPoweroff.Serialize(new PoweroffInput(30))
            });
            journal.Start(taskId);

            var commands = new List<NativeCommand>();
            var host = new OperationHost(root, (command, _) =>
            {
                commands.Add(command);
                return Task.FromResult(new NativeResult(0, "Running as unit: dms-system-poweroff-xxx\n", ""));
            });

            var context = new MachineWorkflowContext(taskId, null, _ => Task.CompletedTask);
            var result = await new AgentPoweroff(state, journal, host).PrepareAsync(taskId, context, new PoweroffInput(30), CancellationToken.None);

            Assert.IsTrue(result.Complete);
            StringAssert.Contains(result.Summary, "Poweroff scheduled in 30s countdown.");
            Assert.HasCount(1, commands);
            Assert.AreEqual("systemd-run", commands[0].Executable);
            Assert.IsTrue(commands[0].Arguments.Contains("--on-active=30s"));
            Assert.IsTrue(commands[0].Arguments.Contains("/usr/bin/systemctl"));
            Assert.IsTrue(commands[0].Arguments.Contains("poweroff"));
            Assert.IsTrue(commands[0].Arguments.Contains("--no-wall"));
        }
        finally
        {
            journal?.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task PoweroffCliHelpSupportsAliases()
    {
        var poweroffHelp = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "system", "poweroff", "--help"]);
        Assert.AreEqual(0, poweroffHelp.ProgramReturn, poweroffHelp.StdErr);
        StringAssert.Contains(poweroffHelp.StdOut, "--delay-seconds");
        StringAssert.Contains(poweroffHelp.StdOut, "--countdown-seconds");

        var shutdownHelp = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "system", "shutdown", "--help"]);
        Assert.AreEqual(0, shutdownHelp.ProgramReturn, shutdownHelp.StdErr);
        Assert.AreEqual(poweroffHelp.StdOut, shutdownHelp.StdOut);
    }
}
