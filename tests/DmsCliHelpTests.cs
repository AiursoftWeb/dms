using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using System.Reflection;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DmsCliHelpTests
{
    [TestMethod]
    public async Task DeclaredCommandsResolveToTheirPublicWorkflowAndCliHelp()
    {
        var registry = new MachineWorkflowRegistry();
        var cli = DmsCommandApp.Create();
        foreach (var field in typeof(OperationsWorkflows).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<WorkflowCommandAttribute>() is not { } command) continue;
            var definition = (WorkflowDefinition)field.GetValue(null)!;
            Assert.IsTrue(definition.PublicEntry, definition.Name);
            Assert.AreSame(definition, registry.FindDefinition(definition.Name), definition.Name);
            var help = await cli.TestRunAsync([.. definition.Name.Split('.'), "--help"]);
            Assert.AreEqual(0, help.ProgramReturn, help.StdErr);
            StringAssert.Contains(help.StdOut, command.Description);
        }
    }

    [TestMethod]
    [DataRow("", "check approvals")]
    [DataRow("access", "its roles, and its effective permissions")]
    [DataRow("artifact", "verify its SHA-256 checksum")]
    [DataRow("lockbox", "eligible approvers")]
    [DataRow("machine filesystem", "Permanently remove")]
    [DataRow("machine box", "set-git-ssh-identity")]
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
    public async Task TraceHelpShowsLogAsTheDefault()
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["workflow", "trace", "--help"]);
        Assert.AreEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdOut, "[default: Log]");
    }

    [TestMethod]
    [DataRow("lockbox", "history", "lifecycle events")]
    [DataRow("workflow", "history", "creations and state changes")]
    public async Task HistoryHelpDescribesScopeAndNameFilters(string group, string command, string description)
    {
        var result = await DmsCommandApp.Create().TestRunAsync([group, command, "--help"]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        StringAssert.Contains(result.StdOut, description);
        StringAssert.Contains(result.StdOut, "--requester");
        StringAssert.Contains(result.StdOut, "--machine");
        StringAssert.Contains(result.StdOut, "7 days before --until");
    }

    [TestMethod]
    [DataRow("operation get-workflow", "get-workflow")]
    [DataRow("operation get-callstack", "get-callstack")]
    [DataRow("operation get-trace", "get-trace")]
    [DataRow("machine performance cpu-sample --limit 2", "--limit")]
    [DataRow("machine performance dstat-sample --limit 2", "--limit")]
    [DataRow("machine nvidia sample --limit 2", "--limit")]
    [DataRow("machine networking tls-check --host example.test --url https://example.test", "--url")]
    public async Task RetiredCommandsAndParametersAreRejected(string invocation, string retiredToken)
    {
        var result = await DmsCommandApp.Create().TestRunAsync(invocation.Split(' '));
        Assert.AreNotEqual(0, result.ProgramReturn);
        StringAssert.Contains(result.StdErr, $"Unrecognized command or argument '{retiredToken}'");
    }

    [TestMethod]
    [DataRow("", "files uploaded to DMS or exported from a machine to DMS")]
    [DataRow("artifact ls", "dms artifact download --id <ArtifactId> --file ./report.tar")]
    [DataRow("machine filesystem ls", "The receipt shows how to follow approval")]
    [DataRow("workflow result", "View a directory listing")]
    [DataRow("machine soap fetch-wsdl", "--service")]
    [DataRow("machine soap query", "--args-file")]
    [DataRow("machine soap call-test", "test-only endpoint")]
    [DataRow("soap inspect-wsdl", "--artifact-id")]
    [DataRow("soap diff-wsdl", "--before")]
    [DataRow("xml validate", "--schema-artifact-id")]
    [DataRow("xml assert-field", "--expect")]
    [DataRow("xml assert-field", "{urn:example}response")]
    public async Task HelpLetsUsersFindDirectoryAndArtifactCommands(string path, string guidance)
    {
        var args = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = await DmsCommandApp.Create().TestRunAsync([.. args, "--help"]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        StringAssert.Contains(result.StdOut, guidance);
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
