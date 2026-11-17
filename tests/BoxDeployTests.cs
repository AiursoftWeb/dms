using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class BoxDeployTests
{
    [TestMethod]
    public void TypedDefaultsAndHighRiskAreSharedByTheControllerAndAgent()
    {
        Assert.AreEqual(new BoxDeployInput(), MachineWorkflows.BoxDeploy.ReadInput("{}"));
        Assert.AreEqual(RiskLevel.High, MachineWorkflows.BoxDeploy.Risk);
        Assert.IsTrue(MachineWorkflows.BoxDeploy.PublicEntry);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.BoxDeploy.Name));
        Assert.Throws<JsonException>(() => MachineWorkflows.BoxDeploy.ReadInput("{\"Path\":\"/tmp/other\"}"));
        Assert.Throws<JsonException>(() => MachineWorkflows.BoxDeploy.ReadInput("{\"Command\":\"id\"}"));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.BoxDeploy.Serialize(new BoxDeployInput(-1)));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.BoxDeploy.Serialize(new BoxDeployInput(5)));
        foreach (var filter in new[] { "", "--all", "a b", "*", "$(id)", "a;id", "a\nb", new string('a', 129) })
            Assert.Throws<ArgumentException>(() => MachineWorkflows.BoxDeploy.Serialize(new BoxDeployInput(Filter: filter)));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task PullsRealGitFastForwardThenExecutesTheUpdatedScriptWithIndependentChildIds(int stage)
    {
        using var box = new Box();
        var before = box.Revision(box.Repository);
        box.Advance();
        var result = MachineWorkflows.BoxDeploy.ReadResult(await box.RunAsync(new BoxDeployInput(stage, "dms.worker-1")));
        Assert.AreEqual(before, result.PreviousCommit);
        Assert.AreEqual(box.Revision(box.Source), result.Commit);
        Assert.AreNotEqual(result.PreviousCommit, result.Commit);
        Assert.AreEqual(box.Repository, result.Repository);
        Assert.AreEqual(stage, result.Stage);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(result.HealthVerified);
        CollectionAssert.AreEqual(new[] { "--stage", stage.ToString(), "--filter", "dms.worker-1" }, File.ReadAllLines(box.PathOf("deployment-arguments")));
        Assert.AreEqual("updated", File.ReadAllText(box.PathOf("deployment-version")).Trim());
        Assert.AreEqual("private-value", File.ReadAllText(box.PathOf(".env.prod")));
        Assert.IsFalse(box.Events.Any(e => e.IsTrace && e.Message.Contains("private-value", StringComparison.Ordinal)));
        var children = box.Events.Where(e => !e.IsTrace && e.Definition == WorkflowNames.Process && e.State == RunState.Succeeded).ToArray();
        Assert.HasCount(4, children);
        Assert.HasCount(4, children.Select(e => e.WorkflowId).Distinct());
        Assert.IsTrue(children.All(e => e.ParentId == box.Id));
        Assert.IsGreaterThan(8192, box.Events.Where(e => e.Source == "stdout").Sum(e => e.Message.Length));
        Assert.IsTrue(box.Events.Any(e => e.Message.Contains("health must be checked separately", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DefaultDeployOmitsTheFilterRatherThanPassingAnEmptyOrWildcardArgument()
    {
        using var box = new Box();
        var result = MachineWorkflows.BoxDeploy.ReadResult(await box.RunAsync(new BoxDeployInput()));
        CollectionAssert.AreEqual(new[] { "--stage", "4" }, File.ReadAllLines(box.PathOf("deployment-arguments")));
        Assert.IsNull(result.Filter);
        Assert.AreEqual(result.PreviousCommit, result.Commit);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("ambiguous")]
    [DataRow("linked")]
    public async Task MissingAmbiguousAndLinkedCandidatesNeverUpdateOrDeploy(string problem)
    {
        using var box = new Box();
        if (problem == "missing") Directory.Move(box.Repository, Path.Combine(box.Root, "unrelated"));
        if (problem == "ambiguous")
        {
            var second = Path.Combine(box.Root, "Box-other");
            Directory.CreateDirectory(Path.Combine(second, ".git"));
            File.WriteAllText(Path.Combine(second, "deploy.sh"), "exit 99");
        }
        if (problem == "linked")
        {
            var outside = Path.Combine(box.Directory, "outside");
            Directory.Move(box.Repository, outside);
            Directory.CreateSymbolicLink(box.Repository, outside);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => box.RunAsync(new BoxDeployInput()));
        Assert.IsFalse(File.Exists(Path.Combine(box.Directory, "pulled")));
        Assert.IsFalse(box.Events.Any(e => e.Definition == WorkflowNames.Process));
    }

    [TestMethod]
    [DataRow("origin")]
    [DataRow("branch")]
    [DataRow("dirty")]
    [DataRow("staged")]
    [DataRow("detached")]
    public async Task UntrustedOrLocallyModifiedCheckoutsFailBeforePulling(string problem)
    {
        using var box = new Box();
        if (problem == "origin") box.Git(box.Repository, "remote", "set-url", "origin", "https://private-value:secret@example.test/Box.git");
        if (problem == "branch") box.Git(box.Repository, "checkout", "-b", "work");
        if (problem == "detached") box.Git(box.Repository, "checkout", "--detach");
        if (problem is "dirty" or "staged")
        {
            File.AppendAllText(box.PathOf("deploy.sh"), "\n# local edit");
            if (problem == "staged") box.Git(box.Repository, "add", "deploy.sh");
        }
        await Assert.ThrowsAsync<ChildWorkflowException>(() => box.RunAsync(new BoxDeployInput()));
        Assert.IsFalse(File.Exists(Path.Combine(box.Directory, "pulled")));
        Assert.IsFalse(File.Exists(box.PathOf("deployment-arguments")));
        Assert.IsFalse(box.Events.Any(e => e.IsTrace && e.Message.Contains("private-value", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DivergedMasterDoesNotMergeResetOrDeploy()
    {
        using var box = new Box();
        box.Advance();
        File.WriteAllText(box.PathOf("local.txt"), "local commit");
        box.Git(box.Repository, "add", "local.txt");
        box.Git(box.Repository, "commit", "-m", "Local change");
        var local = box.Revision(box.Repository);
        await Assert.ThrowsAsync<ChildWorkflowException>(() => box.RunAsync(new BoxDeployInput()));
        Assert.AreEqual(local, box.Revision(box.Repository));
        Assert.IsFalse(File.Exists(box.PathOf("deployment-arguments")));
        Assert.IsTrue(box.Events.Any(e => e.IsTrace && e.Message.Contains("fast-forward", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task ScriptFailureRetainsExitEvidenceAndNeverReportsSuccess()
    {
        using var box = new Box();
        File.WriteAllText(box.PathOf("fail"), "");
        var error = await Assert.ThrowsAsync<ChildWorkflowException>(() => box.RunAsync(new BoxDeployInput()));
        StringAssert.Contains(error.Result, "code 17");
        Assert.AreEqual(RunState.Failed, box.Events.Last(e => e.WorkflowId == box.Id && !e.IsTrace).State);
        Assert.IsTrue(box.Events.Any(e => e.Message.Contains("Earlier steps may have completed", StringComparison.Ordinal)));
        Assert.HasCount(1, File.ReadAllLines(box.PathOf("deployment-count")));
    }

    [TestMethod]
    public async Task MachineLockRejectsAConcurrentDeployAndCancellationRequiresVerification()
    {
        using var box = new Box();
        File.WriteAllText(box.PathOf("hold"), "");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = box.RunAsync(new BoxDeployInput(), stop.Token);
        try
        {
            while (!File.Exists(box.PathOf("deployment-arguments"))) await Task.Delay(20, stop.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask)
                .RunAsync(new BoxDeployWorkflow(new BoxDeployInput(), box.Root, box.Executable), stop.Token));
        }
        finally { await stop.CancelAsync(); }
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => first);
        Assert.AreEqual(RunState.NeedsVerification, box.Events.Last(e => e.WorkflowId == box.Id && !e.IsTrace).State);
        Assert.HasCount(1, File.ReadAllLines(box.PathOf("deployment-count")));
    }

    private sealed class Box : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("dms-box-deploy-").FullName;
        public string Root => Path.Combine(Directory, "swarm-vol");
        public string Repository => Path.Combine(Root, "Box with spaces");
        public string Source => Path.Combine(Directory, "source");
        public string Executable => Path.Combine(Directory, "git");
        public Guid Id { get; } = Guid.NewGuid();
        public ConcurrentQueue<WorkflowEvent> Events { get; } = new();
        public string PathOf(string name) => Path.Combine(Repository, name);

        public Box()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box deployment requires Linux.");
            System.IO.Directory.CreateDirectory(Root);
            System.IO.Directory.CreateDirectory(Source);
            Git(Source, "init", "--initial-branch=master");
            File.WriteAllText(Path.Combine(Source, ".gitignore"), ".env.prod\ndeployment-*\nhold\nfail\n");
            WriteDeploy("initial");
            Git(Source, "add", "."); Git(Source, "commit", "-m", "Initial Box");
            Git(Directory, "clone", "--bare", Source, Path.Combine(Directory, "upstream.git"));
            Git(Directory, "clone", Path.Combine(Directory, "upstream.git"), Repository);
            Git(Repository, "remote", "set-url", "origin", "https://gitlab.aiursoft.com/aiursoft/boxforglobal.git");
            File.WriteAllText(PathOf(".env.prod"), "private-value");
            // Only the transport is local: preflight, diff, fetch and fast-forward use real Git.
            File.WriteAllText(Executable, """
                #!/usr/bin/python3
                import os, pathlib, sys
                directory = pathlib.Path(__file__).parent
                args = sys.argv[1:]
                if 'pull' in args:
                    (directory / 'pulled').write_text('once')
                    args[args.index('origin')] = str(directory / 'upstream.git')
                os.execv('/usr/bin/git', ['/usr/bin/git', *args])
                """);
            File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        private void WriteDeploy(string version) => File.WriteAllText(Path.Combine(Source, "deploy.sh"), """
            #!/bin/bash
            set -euo pipefail
            printf '%s\n' "$@" > deployment-arguments
            echo once >> deployment-count
            if [ -f hold ]; then sleep 60; fi
            if [ -f fail ]; then exit 17; fi
            printf '%09000d\n' 0
            """ + "\nprintf '%s\\n' '" + version + "' > deployment-version\n");

        public void Advance()
        {
            WriteDeploy("updated"); Git(Source, "add", "deploy.sh"); Git(Source, "commit", "-m", "Reviewed update");
            Git(Source, "push", Path.Combine(Directory, "upstream.git"), "master");
        }

        public string Revision(string directory) => Git(directory, "rev-parse", "HEAD").Trim();

        public string Git(string directory, params string[] args)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("/usr/bin/git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-c", "user.name=DMS Test", "-c", "user.email=dms@example.test", "-c", "commit.gpgsign=false", "-C", directory }.Concat(args))
                process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd();
            process.WaitForExit(); Assert.AreEqual(0, process.ExitCode, error);
            return output;
        }

        public Task<string> RunAsync(BoxDeployInput input, CancellationToken token = default) =>
            new MachineWorkflowContext(Id, null, e => { Events.Enqueue(e); return Task.CompletedTask; })
                .RunAsync(new BoxDeployWorkflow(input, Root, Executable), token);

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
