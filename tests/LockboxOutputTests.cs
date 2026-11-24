using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class LockboxOutputTests
{
    private static LockboxDetails SampleLockbox(Guid? legacyApprovalWorkflowId = null)
    {
        return new LockboxDetails(
            LockboxId: LockboxId.Parse("lb/c522a0a4-e6d7-4379-8dda-9c50999efda3"),
            ClientRequestId: Guid.Parse("fca6e49e-65ed-4ee5-9f35-e28864279369"),
            RequesterId: "157708d8-2514-4c8d-8a00-121aa720f606",
            Definition: "machine.box.configure-git",
            Version: "1",
            Risk: RiskLevel.Medium,
            Parameters: """{"RequestId":"fca6e49e-65ed-4ee5-9f35-e28864279369","Workflow":"machine.box.configure-git","AllMachines":false,"Reason":"configure Git SSH identity for China to pull Box repositories","MachineIds":["69a58ba0-2944-4e24-b34f-32d1d1338155"],"Input":"{\"IdentityFile\":\"/home/cici/.ssh/id_rsa\",\"KnownHostsFile\":\"/home/cici/.ssh/known_hosts\"}"}""",
            TargetMachines: """["69a58ba0-2944-4e24-b34f-32d1d1338155"]""",
            Reason: "configure Git SSH identity for China to pull Box repositories",
            CreatedAt: new DateTime(2026, 11, 24, 7, 0, 0, DateTimeKind.Utc),
            Decision: DecisionState.Approved,
            ApproverId: "f591da20-f307-48ed-ab9c-9c03410d87b4",
            Automatic: false,
            DecidedAt: new DateTime(2026, 11, 24, 7, 3, 50, DateTimeKind.Utc),
            DecisionReason: "approve Git SSH identity for China",
            WorkflowId: Guid.Parse("3249337a-46f4-4deb-a0c7-8ab30a61755f"),
            LegacyApprovalWorkflowId: legacyApprovalWorkflowId
        );
    }

    [TestMethod]
    public void LockboxDefaultTableHidesParametersAndTargetMachines()
    {
        var box = SampleLockbox(legacyApprovalWorkflowId: null);
        var response = new DmsResponse { Message = "Decision recorded.", Data = box };

        using var output = new StringWriter();
        DmsOutputWriter.Write(output, response, OutputFormat.Table, callstack: false, color: false, width: 120, verbose: false);
        var text = output.ToString();

        StringAssert.Contains(text, "lb/c522a0a4-e6d7-4379-8dda-9c50999efda3");
        StringAssert.Contains(text, "fca6e49e-65ed-4ee5-9f35-e28864279369");
        StringAssert.Contains(text, "machine.box.configure-git");
        StringAssert.Contains(text, "Approved");
        StringAssert.Contains(text, "3249337a-46f4-4deb-a0c7-8ab30a61755f");

        Assert.IsFalse(text.Contains("\nParameters\n", StringComparison.Ordinal), "Default output should not contain Parameters table header.");
        Assert.IsFalse(text.Contains("\nTargetMachines\n", StringComparison.Ordinal), "Default output should not contain TargetMachines table header.");
        Assert.IsFalse(text.Contains("\nMachineIds\n", StringComparison.Ordinal), "Default output should not contain nested MachineIds.");
        Assert.IsFalse(text.Contains("\nInput\n", StringComparison.Ordinal), "Default output should not contain nested Input.");
        Assert.IsFalse(text.Contains("LegacyApprovalWorkflowId", StringComparison.Ordinal), "Null LegacyApprovalWorkflowId should be omitted.");
    }

    [TestMethod]
    public void LockboxVerboseTableIncludesParametersAndTargetMachines()
    {
        var box = SampleLockbox(legacyApprovalWorkflowId: null);
        var response = new DmsResponse { Message = "Decision recorded.", Data = box };

        using var output = new StringWriter();
        DmsOutputWriter.Write(output, response, OutputFormat.Table, callstack: false, color: false, width: 120, verbose: true);
        var text = output.ToString();

        StringAssert.Contains(text, "lb/c522a0a4-e6d7-4379-8dda-9c50999efda3");
        StringAssert.Contains(text, "Parameters");
        StringAssert.Contains(text, "TargetMachines");
        StringAssert.Contains(text, "MachineIds");
        StringAssert.Contains(text, "Input");
        StringAssert.Contains(text, "/home/cici/.ssh/id_rsa");
    }

    [TestMethod]
    public void LockboxTableShowsLegacyApprovalWorkflowIdWhenPresent()
    {
        var legacyId = Guid.NewGuid();
        var box = SampleLockbox(legacyApprovalWorkflowId: legacyId);
        var response = new DmsResponse { Message = "Decision recorded.", Data = box };

        using var output = new StringWriter();
        DmsOutputWriter.Write(output, response, OutputFormat.Table, callstack: false, color: false, width: 120, verbose: false);
        var text = output.ToString();

        StringAssert.Contains(text, "LegacyApprovalWorkflowId");
        StringAssert.Contains(text, legacyId.ToString());
    }

    [TestMethod]
    [DataRow("approve")]
    [DataRow("deny")]
    [DataRow("withdraw")]
    public async Task DecisionCommandsExposeVerboseOption(string action)
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["lockbox", action, "--help"]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        StringAssert.Contains(result.StdOut, "--verbose");
        StringAssert.Contains(result.StdOut, "Include full execution parameters and target machines in the output.");
    }

    [TestMethod]
    public async Task GetCommandDoesNotNeedVerboseOption()
    {
        var result = await DmsCommandApp.Create().TestRunAsync(["lockbox", "get", "--help"]);
        Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
        Assert.IsFalse(result.StdOut.Contains("--verbose", StringComparison.Ordinal));
    }
}
