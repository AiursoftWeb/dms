using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class FilesystemVolumeDirTests
{
    private static async Task<OperationResult> RunAsync<TInput>(
        MachineWorkflowDefinition<TInput, OperationResult> definition,
        TInput input,
        OperationHost host)
    {
        var registry = new MachineWorkflowRegistry();
        var agent = new AgentEnvironment(Guid.NewGuid(), "1.0.0", 0, 0, Operations: host);
        var workflow = registry.Create(definition.Name, definition.Serialize(input), agent);
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask, agent, registry);
        return definition.ReadResult(await context.RunAsync(workflow, CancellationToken.None));
    }

    [TestMethod]
    public async Task MkdirAppliesModeAndOwnerOnlyToNewlyCreatedDirectories()
    {
        Assert.AreEqual(RiskLevel.Medium, OperationsWorkflows.FilesystemMkdir.Risk);
        Assert.AreEqual("2", OperationsWorkflows.FilesystemMkdir.Version);

        var root = Directory.CreateTempSubdirectory("dms-mkdir-test-").FullName;
        try
        {
            var existingParent = Path.Combine(root, "existing");
            Directory.CreateDirectory(existingParent);
            var child1 = Path.Combine(existingParent, "new-a");
            var child2 = Path.Combine(child1, "new-b");

            List<NativeCommand> commands = [];
            var host = new OperationHost(native: (cmd, _) =>
            {
                commands.Add(cmd);
                if (cmd.Executable == "mkdir")
                    Directory.CreateDirectory(cmd.Arguments[^1]);
                return Task.FromResult(new NativeResult(0, "", ""));
            });

            var result = await RunAsync(OperationsWorkflows.FilesystemMkdir,
                new MakeDirectoryInput(child2, Parents: true, Mode: 488 /* 0750 */, Uid: 1000, Gid: 2000), host);

            Assert.IsTrue(result.Complete);
            var chmodTargets = commands.Where(c => c.Executable == "chmod").Select(c => c.Arguments[^1]).ToArray();
            var chownTargets = commands.Where(c => c.Executable == "chown").Select(c => c.Arguments[^1]).ToArray();
            CollectionAssert.AreEqual(new[] { child1, child2 }, chmodTargets);
            CollectionAssert.AreEqual(new[] { child1, child2 }, chownTargets);
            CollectionAssert.DoesNotContain(chmodTargets, existingParent);
            CollectionAssert.DoesNotContain(chownTargets, existingParent);
            Assert.IsTrue(commands.Where(c => c.Executable == "chown").All(c => c.Arguments.Contains("--no-dereference") && c.Arguments.Contains("1000:2000")));
            Assert.IsTrue(commands.Where(c => c.Executable == "chmod").All(c => c.Arguments.Contains("0750")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("/swarm-vol")]
    [DataRow("/etc/app")]
    [DataRow("/swarm-vol/boxforglobal")]
    [DataRow("/swarm-vol/BoxForProart/stage4")]
    [DataRow("/swarm-vol/.dms-box-deploy.lock")]
    public void PrepareVolumeDirRejectsPathsOutsideAllowedVolumeSubdirectories(string badPath)
    {
        Assert.Throws<ArgumentException>(() =>
            OperationsWorkflows.FilesystemPrepareVolumeDir.Serialize(new PrepareVolumeDirectoryInput(badPath)));
    }

    [TestMethod]
    public async Task PrepareVolumeDirIsIdempotentAndNeverTouchesVolumeRoot()
    {
        Assert.AreEqual(RiskLevel.Medium, OperationsWorkflows.FilesystemPrepareVolumeDir.Risk);
        var root = Directory.CreateTempSubdirectory("dms-vol-test-").FullName;
        try
        {
            var volumeRoot = Path.Combine(root, "swarm-vol");
            Directory.CreateDirectory(volumeRoot);
            var logicalTarget = "/swarm-vol/ollama-app/cache/models";
            var physicalMid1 = Path.Combine(volumeRoot, "ollama-app");
            var physicalMid2 = Path.Combine(physicalMid1, "cache");
            var physicalTarget = Path.Combine(physicalMid2, "models");

            List<NativeCommand> commands = [];
            var host = new OperationHost(root: root, native: (cmd, _) =>
            {
                commands.Add(cmd);
                if (cmd.Executable == "mkdir")
                    Directory.CreateDirectory(cmd.Arguments[^1]);
                return Task.FromResult(new NativeResult(0, "", ""));
            });

            // First run: creates all 3 subdirs and chmod/chowns them (never /swarm-vol)
            var first = await RunAsync(OperationsWorkflows.FilesystemPrepareVolumeDir,
                new PrepareVolumeDirectoryInput(logicalTarget, Mode: 493 /* 0755 */, Uid: 999, Gid: 999), host);
            Assert.IsTrue(first.Complete);
            Assert.AreEqual(3, first.Facts!.Value.GetProperty("CreatedCount").GetInt32());
            var firstChownTargets = commands.Where(c => c.Executable == "chown").Select(c => c.Arguments[^1]).ToArray();
            CollectionAssert.AreEqual(new[] { physicalMid1, physicalMid2, physicalTarget }, firstChownTargets);
            CollectionAssert.DoesNotContain(firstChownTargets, volumeRoot);

            // Second run: idempotent — skips mkdir, ensures permissions on target only
            commands.Clear();
            var second = await RunAsync(OperationsWorkflows.FilesystemPrepareVolumeDir,
                new PrepareVolumeDirectoryInput(logicalTarget, Mode: 488 /* 0750 */, Uid: 1001), host);
            Assert.IsTrue(second.Complete);
            Assert.AreEqual(0, second.Facts!.Value.GetProperty("CreatedCount").GetInt32());
            Assert.IsFalse(commands.Any(c => c.Executable == "mkdir"));
            CollectionAssert.AreEqual(new[] { physicalTarget }, commands.Where(c => c.Executable == "chmod").Select(c => c.Arguments[^1]).ToArray());
            CollectionAssert.AreEqual(new[] { physicalTarget }, commands.Where(c => c.Executable == "chown").Select(c => c.Arguments[^1]).ToArray());
            Assert.IsTrue(commands.Single(c => c.Executable == "chown").Arguments.Contains("1001"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CliSupportsOctalAndDecimalModesAndPrepareVolumeDirHelp()
    {
        var cli = DmsCommandApp.Create();
        var mkdirHelp = await cli.TestRunAsync(["machine", "filesystem", "mkdir", "--help"]);
        Assert.AreEqual(0, mkdirHelp.ProgramReturn, mkdirHelp.StdErr);
        StringAssert.Contains(mkdirHelp.StdOut, "--uid");
        StringAssert.Contains(mkdirHelp.StdOut, "--gid");
        StringAssert.Contains(mkdirHelp.StdOut, "0755");

        var prepHelp = await cli.TestRunAsync(["machine", "filesystem", "prepare-volume-dir", "--help"]);
        Assert.AreEqual(0, prepHelp.ProgramReturn, prepHelp.StdErr);
        StringAssert.Contains(prepHelp.StdOut, "/swarm-vol/");
        StringAssert.Contains(prepHelp.StdOut, "--uid");
        StringAssert.Contains(prepHelp.StdOut, "--gid");

        // Invalid octal digit (0888) rejected by CLI parser
        var badMode = await cli.TestRunAsync(["machine", "filesystem", "prepare-volume-dir", "--path", "/swarm-vol/app", "--mode", "0888", "--reason", "test"]);
        Assert.AreNotEqual(0, badMode.ProgramReturn);
        StringAssert.Contains(badMode.StdErr, "Mode must be octal");
    }
}
