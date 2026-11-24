using System.Collections.Concurrent;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class StorageWorkflowTests
{
    [TestMethod]
    public async Task DirectoryUsageTreatsPathsLiterallyDoesNotFollowLinksAndOnlyTracesMetadata()
    {
        var directory = Directory.CreateTempSubdirectory("dms-du-").FullName;
        try
        {
            var root = Path.Combine(directory, "directory with spaces;$(printf literal)");
            Directory.CreateDirectory(Path.Combine(root, "child"));
            File.WriteAllText(Path.Combine(root, "child", "file"), "never-read-this-file-content");
            var outside = Path.Combine(directory, "outside");
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "not-followed"), new byte[1024 * 1024]);
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);
            var events = new ConcurrentQueue<WorkflowEvent>();
            var id = Guid.NewGuid();
            var result = await new MachineWorkflowContext(id, null, e => { events.Enqueue(e); return Task.CompletedTask; })
                .RunAsync(new DirectoryUsageWorkflow(new DirectoryUsageInput(root)), CancellationToken.None);
            Assert.AreEqual(0, MachineWorkflows.DirectoryUsage.ReadResult(result).ExitCode);
            var trace = string.Join("", events.Where(e => e.Source == "stdout").Select(e => e.Message));
            StringAssert.Contains(trace, root);
            StringAssert.Contains(trace, Path.Combine(root, "child"));
            Assert.DoesNotContain("not-followed", trace);
            Assert.DoesNotContain("never-read-this-file-content", JsonSerializer.Serialize(events.ToArray()));
            Assert.DoesNotContain(root, result);
            var child = events.Single(e => e.Definition == WorkflowNames.Process && e.State == RunState.Succeeded && !e.IsTrace);
            Assert.AreEqual(id, child.ParentId);
            Assert.AreNotEqual(id, child.WorkflowId);
            var diagnostics = string.Join('\n', events.Where(e => e.IsTrace && e.Source == "engine").Select(e => e.Message));
            StringAssert.Contains(diagnostics, "path=" + JsonSerializer.Serialize(root));
            StringAssert.Contains(diagnostics, "max_depth=1; timeout_seconds=60");
            StringAssert.Contains(diagnostics, "exit_code=0; elapsed_ms=");
            StringAssert.Contains(diagnostics, "stdout_characters=");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task FailedDirectoryScanExplainsItsScopeAndLinksToTheNativeEvidence()
    {
        var directory = Directory.CreateTempSubdirectory("dms-du-missing-").FullName;
        try
        {
            var events = new ConcurrentQueue<WorkflowEvent>(); var id = Guid.NewGuid();
            var context = new MachineWorkflowContext(id, null, e => { events.Enqueue(e); return Task.CompletedTask; });
            var failure = await Assert.ThrowsAsync<ChildWorkflowException>(() => context.RunAsync(
                new DirectoryUsageWorkflow(new DirectoryUsageInput(Path.Combine(directory, "missing"))), CancellationToken.None));
            var trace = string.Join('\n', events.Where(e => e.IsTrace).Select(e => e.Message));
            StringAssert.Contains(trace, "Any emitted byte counts and native errors remain in subworkflow " + failure.WorkflowId);
            StringAssert.Contains(trace, "Exit code 124 means the 60-second scan limit");
            StringAssert.Contains(trace, "retry a narrower --path");
            Assert.IsTrue(events.Any(e => e.WorkflowId == failure.WorkflowId && e.Source == "stderr" && e.Message.Contains("missing", StringComparison.Ordinal)));
            Assert.AreEqual(RunState.Failed, events.Last(e => !e.IsTrace && e.WorkflowId == id).State);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(124)]
    public async Task FailedProcessRetainsPartialOutputAndReportsExitAndVolume(int exitCode)
    {
        var events = new ConcurrentQueue<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; });
        var command = exitCode == 1
            ? new ProcessWorkflow("/bin/sh", ["-c", "printf 'root 47%%\n'; printf 'df: /root/.gvfs: Transport endpoint is not connected\n' >&2; exit 1"])
            : new ProcessWorkflow("/usr/bin/timeout", ["0.1s", "/bin/sh", "-c", "printf 'partial bytes\n'; sleep 5"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync(command, CancellationToken.None));
        var trace = string.Join('\n', events.Where(e => e.IsTrace).Select(e => e.Message));
        StringAssert.Contains(trace, $"exit_code={exitCode}; elapsed_ms=");
        StringAssert.Contains(trace, "stdout_characters=");
        StringAssert.Contains(trace, "partial results");
        if (exitCode == 1)
        {
            StringAssert.Contains(trace, "root 47%"); StringAssert.Contains(trace, "/root/.gvfs");
            StringAssert.Contains(trace, "stdout_characters=9; stderr_characters=53");
        }
        else StringAssert.Contains(trace, "partial bytes");
        Assert.AreEqual(RunState.Failed, events.Last(e => !e.IsTrace).State);
    }

    [TestMethod]
    public void StorageAndGitInputsExposeOnlyTheirTypedParameters()
    {
        var registry = new MachineWorkflowRegistry();
        foreach (var definition in new WorkflowDefinition[] { MachineWorkflows.DirectoryUsage, MachineWorkflows.DockerDiskUsage, MachineWorkflows.BoxConfigureGit })
        {
            Assert.IsTrue(definition.PublicEntry);
            Assert.IsTrue(definition.RequiresTargets);
            Assert.IsTrue(registry.Contains(definition.Name));
            Assert.Throws<JsonException>(() => definition.Validate("{\"Command\":\"rm -rf /\"}"));
        }
        Assert.AreEqual(RiskLevel.Low, MachineWorkflows.DirectoryUsage.Risk);
        Assert.AreEqual(RiskLevel.Low, MachineWorkflows.DockerDiskUsage.Risk);
        Assert.AreEqual(RiskLevel.Medium, MachineWorkflows.BoxConfigureGit.Risk);
        foreach (var path in new[] { "relative", "/tmp/../etc", "/tmp\npath", "", "/tmp//path" })
            Assert.Throws<ArgumentException>(() => new DirectoryUsageInput(path).Validate());
        Assert.Throws<ArgumentException>(() => new DirectoryUsageInput("/", -1).Validate());
        Assert.Throws<ArgumentException>(() => new DirectoryUsageInput("/", 4).Validate());
        foreach (var path in new[] { "/tmp/a b", "/tmp/$HOME", "/tmp/%d", "/tmp/a'b" })
            Assert.Throws<ArgumentException>(() => new BoxGitInput(path, "/etc/known_hosts").Validate());
    }

    [TestMethod]
    public async Task DiskFreeStreamsOnlyPhysicalLocalFilesystemRows()
    {
        var events = new ConcurrentQueue<WorkflowEvent>();
        var result = await new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; })
            .RunAsync(new DiskFreeWorkflow(), CancellationToken.None);
        Assert.AreEqual(0, MachineWorkflows.DiskFree.ReadResult(result).ExitCode);
        var trace = string.Join("", events.Where(e => e.Source == "stdout").Select(e => e.Message));
        StringAssert.Contains(trace, "Filesystem");
        Assert.IsFalse(trace.Split('\n').Any(line => line.StartsWith("overlay ", StringComparison.Ordinal)));
        Assert.AreEqual("", MachineWorkflows.DiskFree.ReadResult(result).Output);
    }
}
