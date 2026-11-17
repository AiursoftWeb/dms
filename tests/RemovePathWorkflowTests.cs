using System.Collections.Concurrent;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class RemovePathWorkflowTests
{
    [TestMethod]
    public async Task RemovesOnlyTheLiteralFileAndReportsItsChildWorkflowInTrace()
    {
        var directory = Directory.CreateTempSubdirectory("dms-rm-").FullName;
        try
        {
            var path = Path.Combine(directory, "literal *;$(printf injected)");
            File.WriteAllText(path, "file-content-must-not-be-archived");
            var keep = Path.Combine(directory, "keep"); File.WriteAllText(keep, "keep");
            var id = Guid.NewGuid(); var events = new ConcurrentQueue<WorkflowEvent>();
            var result = await RunAsync(new RemovePathInput(path), id, events);
            Assert.IsFalse(File.Exists(path)); Assert.IsTrue(File.Exists(keep));
            Assert.AreEqual(0, MachineWorkflows.RemovePath.ReadResult(result).ExitCode);
            Assert.AreEqual("", MachineWorkflows.RemovePath.ReadResult(result).Output);
            var child = events.Single(e => e.Definition == WorkflowNames.Process && e.State == RunState.Succeeded && !e.IsTrace);
            Assert.AreEqual(id, child.ParentId); Assert.AreNotEqual(id, child.WorkflowId);
            StringAssert.Contains(string.Join("", events.Where(e => e.Source == "stdout").Select(e => e.Message)), path);
            Assert.DoesNotContain("file-content-must-not-be-archived", JsonSerializer.Serialize(events));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task DirectoryRequiresExplicitRecursionAndLinksNeverDeleteTheirDestinations()
    {
        var directory = Directory.CreateTempSubdirectory("dms-rm-").FullName;
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(directory, "target")).FullName;
            File.WriteAllText(Path.Combine(target, "keep"), "keep");
            var remove = Directory.CreateDirectory(Path.Combine(directory, "remove")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(remove, "link"), target);
            var events = new ConcurrentQueue<WorkflowEvent>();
            await Assert.ThrowsAsync<IOException>(() => RunAsync(new RemovePathInput(remove), Guid.NewGuid(), events));
            Assert.IsFalse(events.Any(e => e.Definition == WorkflowNames.Process));
            await RunAsync(new RemovePathInput(remove, true), Guid.NewGuid(), events);
            Assert.IsFalse(Directory.Exists(remove)); Assert.IsTrue(File.Exists(Path.Combine(target, "keep")));
            var link = Path.Combine(directory, "direct-link"); Directory.CreateSymbolicLink(link, target);
            await Assert.ThrowsAsync<IOException>(() => RunAsync(new RemovePathInput(Path.Combine(link, "keep")), Guid.NewGuid(), events));
            await RunAsync(new RemovePathInput(link), Guid.NewGuid(), events);
            Assert.IsFalse(Directory.Exists(link)); Assert.IsTrue(File.Exists(Path.Combine(target, "keep")));
            File.CreateSymbolicLink(link, Path.Combine(directory, "missing-destination"));
            await RunAsync(new RemovePathInput(link), Guid.NewGuid(), events);
            Assert.IsNull(new FileInfo(link).LinkTarget);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void UnsafePathsAndUnexpectedInputsAreRejectedBeforeDispatch()
    {
        foreach (var path in new[] { "/", "//", "/tmp/", "/tmp/..", "/tmp//file", "/tmp/./file", "relative", "", "/tmp\nfile" })
            Assert.Throws<ArgumentException>(() => new RemovePathInput(path, true).Validate());
        Assert.Throws<JsonException>(() => MachineWorkflows.RemovePath.Validate("{\"Path\":\"/tmp/a\",\"Force\":true}"));
        Assert.AreEqual(RiskLevel.High, MachineWorkflows.RemovePath.Risk);
        Assert.IsTrue(MachineWorkflows.RemovePath.PublicEntry);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.RemovePath.Name));
    }

    [TestMethod]
    public async Task MissingPathFailsBeforeStartingAProcess()
    {
        var events = new ConcurrentQueue<WorkflowEvent>();
        await Assert.ThrowsAsync<FileNotFoundException>(() => RunAsync(new RemovePathInput("/tmp/dms-missing-" + Guid.NewGuid().ToString("N")), Guid.NewGuid(), events));
        Assert.IsFalse(events.Any(e => e.Definition == WorkflowNames.Process));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PartialFailureOrInterruptedRemovalNeedsVerification(bool cancel)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Requires Linux process execution.");
            return;
        }
        var directory = Directory.CreateTempSubdirectory("dms-rm-").FullName;
        try
        {
            var target = Path.Combine(directory, "target"); File.WriteAllText(target, "fixture");
            var executable = Path.Combine(directory, "partial-rm");
            await File.WriteAllTextAsync(executable, "#!/bin/sh\n/usr/bin/rm -- \"$(dirname \"$0\")/target\"\necho mutation-started\n" + (cancel ? "/usr/bin/sleep 30\n" : "exit 17\n"));
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var events = new ConcurrentQueue<WorkflowEvent>(); var id = Guid.NewGuid();
            using var stop = new CancellationTokenSource();
            var run = RunAsync(new RemovePathInput(target), id, events, stop.Token, executable);
            if (cancel)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!events.Any(e => e.Source == "stdout" && e.Message.Contains("mutation-started", StringComparison.Ordinal)))
                    await Task.Delay(20, timeout.Token);
                await stop.CancelAsync();
            }
            await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => run);
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual(RunState.NeedsVerification, events.Last(e => e.WorkflowId == id && !e.IsTrace).State);
            Assert.AreEqual(1, events.Count(e => e.Definition == WorkflowNames.Process && e.State == RunState.Running && !e.IsTrace));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Task<string> RunAsync(RemovePathInput input, Guid id, ConcurrentQueue<WorkflowEvent> events,
        CancellationToken token = default, string executable = "/usr/bin/rm") =>
        new MachineWorkflowContext(id, null, e => { events.Enqueue(e); return Task.CompletedTask; })
            .RunAsync(new RemovePathWorkflow(input, executable), token);
}
