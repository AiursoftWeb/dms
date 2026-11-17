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
        }
        finally { Directory.Delete(directory, true); }
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
        Assert.AreEqual(RiskLevel.High, MachineWorkflows.BoxConfigureGit.Risk);
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
