using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DmsCliHelpTests
{
    [TestMethod]
    [DataRow("", "check approvals")]
    [DataRow("access", "its roles, and its effective permissions")]
    [DataRow("artifact", "verify its SHA-256 checksum")]
    [DataRow("lockbox", "eligible approvers")]
    [DataRow("machine filesystem", "Permanently remove")]
    [DataRow("machine swarm service", "Scale a replicated Swarm service to zero")]
    public async Task NavigationShowsHelpWithoutRequiringASubcommand(string path, string description)
    {
        var args = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cli = DmsCommandApp.Create();
        var bare = await cli.TestRunAsync(args);
        var explicitHelp = await cli.TestRunAsync([.. args, "--help"]);

        Assert.AreEqual(0, bare.ProgramReturn);
        Assert.AreEqual(string.Empty, bare.StdErr);
        Assert.AreEqual(explicitHelp.StdOut, bare.StdOut);
        StringAssert.Contains(bare.StdOut, description);
    }

    [TestMethod]
    public async Task KeyReplacementHelpExplainsTheEffectAndTarget()
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["access", "issue-agent-key", "--help"]);
        Assert.AreEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdOut, "invalidate its previous key");
        StringAssert.Contains(result.StdOut, "whose Agent key will be replaced");
    }

    [TestMethod]
    [DataRow("workflow", "trace")]
    [DataRow("operation", "get-trace")]
    public async Task TraceHelpShowsLogAsTheDefault(string group, string command)
    {
        var result = await DmsCommandApp.Create().TestRunAsync([group, command, "--help"]);
        Assert.AreEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdOut, "[default: Log]");
    }

    [TestMethod]
    public void EmptyInventoryCsvRetainsColumnHeaders()
    {
        using var output = new StringWriter();
        DmsOutputWriter.WriteResult(output, new List<MachineSummary>(),
            OutputFormat.Csv, color: false, width: 120);
        Assert.AreEqual("AgentVersion,Disabled,Id,LastSeen,Name\r\n", output.ToString());
    }

    [TestMethod]
    public async Task InvalidCommandsStillReportAnError()
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["access", "whoamii"]);
        Assert.AreNotEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdErr, "whoamii");
    }
}
