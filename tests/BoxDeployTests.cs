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
    [DataRow("multiple-origins")]
    [DataRow("tracked-private-env")]
    [DataRow("branch")]
    [DataRow("dirty")]
    [DataRow("staged")]
    [DataRow("detached")]
    public async Task UntrustedOrLocallyModifiedCheckoutsFailBeforePulling(string problem)
    {
        using var box = new Box();
        if (problem == "origin") box.Git(box.Repository, "remote", "set-url", "origin", "https://private-value:secret@example.test/Box.git");
        if (problem == "multiple-origins")
        {
            box.Git(box.Repository, "remote", "set-url", "origin", "https://private-value:secret@example.test/Box.git");
            box.Git(box.Repository, "config", "--add", "remote.origin.url", "https://gitlab.aiursoft.com/aiursoft/boxforglobal.git");
        }
        if (problem == "tracked-private-env")
        {
            box.Git(box.Repository, "add", "--force", ".env.prod");
            box.Git(box.Repository, "commit", "-m", "Track synthetic environment");
        }
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

    [TestMethod]
    [DataRow("https://gitlab.aiursoft.com/aiursoft/boxforglobal.git")]
    [DataRow("git@gitlab.aiursoft.com:aiursoft/boxforproart.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/aiursoft/boxforglobal.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/aiursoft/boxforchina.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/aiursoft/boxforhl.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/aiursoft/boxforarchive.git")]
    public async Task OfficialHttpsScpAndPublishedSshOriginsAreInspectedAndDeployable(string origin)
    {
        using var box = new Box();
        box.Git(box.Repository, "remote", "set-url", "origin", origin);
        var head = box.Revision(box.Repository);
        Assert.AreEqual("{}", await box.InspectAsync());
        var trace = string.Join("\n", box.Events.Where(e => e.IsTrace).Select(e => e.Message));
        StringAssert.Contains(trace, "official=true");
        StringAssert.Contains(trace, "Branch: master");
        StringAssert.Contains(trace, "Commit: " + head);
        StringAssert.Contains(trace, "TrackedChanges: false");
        StringAssert.Contains(trace, "StagedChanges: false");
        StringAssert.Contains(trace, "PrivateEnvironmentTracked: false");
        if (origin.StartsWith("ssh://", StringComparison.Ordinal)) StringAssert.Contains(trace, "host=ssh.aiursoft.com port=2202");
        Assert.IsFalse(File.Exists(box.PathOf("deployment-arguments")));
        Assert.IsFalse(File.Exists(Path.Combine(box.Root, ".dms-box-deploy.lock")));
        Assert.IsFalse(File.Exists(Path.Combine(box.Directory, "pulled")));
        Assert.AreEqual(head, box.Revision(box.Repository));
        Assert.IsTrue(box.Events.Where(e => !e.IsTrace && e.State == RunState.Succeeded).All(e => !e.Result.Contains("Origin", StringComparison.Ordinal)));
        var result = MachineWorkflows.BoxDeploy.ReadResult(await box.RunAsync(new BoxDeployInput()));
        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    [DataRow("https://private-value:secret-value@gitlab.aiursoft.com/aiursoft/boxforglobal.git?token=query-secret#fragment-secret")]
    [DataRow("ssh://private-value:secret-value@ssh.aiursoft.com:2202/aiursoft/boxforglobal.git")]
    [DataRow("https://gitlab.aiursoft.com.evil.test/private-value/secret-value")]
    [DataRow("https://private-value:secret-value@evil.test/aiursoft/boxforglobal.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:22/aiursoft/boxforglobal.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/other/boxforglobal.git")]
    [DataRow("ssh://git@ssh.aiursoft.com:2202/aiursoft/not-a-box.git")]
    [DataRow("https://gitlab.aiursoft.com/aiursoft/boxforglobal.git\nhttps://private-value:secret-value@evil.test/secret-value")]
    public async Task InspectionRedactsCredentialsAndUnknownOriginsNeverDeploy(string origin)
    {
        using var box = new Box();
        box.Git(box.Repository, "remote", "set-url", "origin", origin);
        Assert.AreEqual("{}", await box.InspectAsync());
        var events = JsonSerializer.Serialize(box.Events.ToArray());
        foreach (var secret in new[] { "private-value", "secret-value", "query-secret", "fragment-secret", "evil.test" })
            Assert.DoesNotContain(secret, events);
        Assert.IsTrue(box.Events.Any(e => e.IsTrace && e.Message.Contains("official=false", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<ChildWorkflowException>(() => box.RunAsync(new BoxDeployInput()));
        Assert.IsFalse(File.Exists(Path.Combine(box.Directory, "pulled")));
        Assert.IsFalse(File.Exists(box.PathOf("deployment-arguments")));
    }

    [TestMethod]
    public async Task InspectionReportsMissingAndMultipleOriginsAndDirtyDetachedStateWithoutUpdating()
    {
        using var box = new Box();
        var head = box.Revision(box.Repository);
        box.Git(box.Repository, "remote", "remove", "origin");
        await box.InspectAsync();
        Assert.IsTrue(box.Events.Any(e => e.IsTrace && e.Message.Contains("OriginCount: 0", StringComparison.Ordinal)));
        box.Events.Clear();
        box.Git(box.Repository, "remote", "add", "origin", "https://gitlab.aiursoft.com/aiursoft/boxforglobal.git");
        box.Git(box.Repository, "config", "--add", "remote.origin.url", "ssh://git@ssh.aiursoft.com:2202/aiursoft/boxforglobal.git");
        box.Git(box.Repository, "checkout", "--detach");
        File.AppendAllText(box.PathOf("deploy.sh"), "\n# staged");
        box.Git(box.Repository, "add", "deploy.sh");
        File.AppendAllText(box.PathOf("deploy.sh"), "\n# unstaged");
        await box.InspectAsync();
        var trace = string.Join("\n", box.Events.Where(e => e.IsTrace).Select(e => e.Message));
        StringAssert.Contains(trace, "OriginCount: 2");
        StringAssert.Contains(trace, "Branch: [detached or unrecognized]");
        StringAssert.Contains(trace, "TrackedChanges: true");
        StringAssert.Contains(trace, "StagedChanges: true");
        Assert.AreEqual(head, box.Revision(box.Repository));
        Assert.IsFalse(File.Exists(Path.Combine(box.Directory, "pulled")));
        Assert.IsFalse(File.Exists(box.PathOf("deployment-arguments")));
        await Assert.ThrowsAsync<ChildWorkflowException>(() => box.RunAsync(new BoxDeployInput()));
    }

    [TestMethod]
    public async Task InspectionUsesSafeStandardDefinitionAndCannotAcceptAnArbitraryPathOrCommand()
    {
        Assert.AreEqual(RiskLevel.Safe, MachineWorkflows.BoxInspect.Risk);
        Assert.IsTrue(MachineWorkflows.BoxInspect.PublicEntry);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.BoxInspect.Name));
        Assert.Throws<JsonException>(() => MachineWorkflows.BoxInspect.ReadInput("{\"Path\":\"/etc\"}"));
        Assert.Throws<JsonException>(() => MachineWorkflows.BoxInspect.ReadInput("{\"Command\":\"id\"}"));
        using var box = new Box();
        Directory.Move(box.Repository, Path.Combine(box.Root, "unrelated"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => box.InspectAsync());
        Assert.IsFalse(box.Events.Any(e => e.Definition == WorkflowNames.Process));
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

        public Task<string> InspectAsync() => new MachineWorkflowContext(Id, null, e => { Events.Enqueue(e); return Task.CompletedTask; })
            .RunAsync(new BoxInspectWorkflow(Root, Executable), CancellationToken.None);

        public Task<string> RunAsync(BoxDeployInput input, CancellationToken token = default) =>
            new MachineWorkflowContext(Id, null, e => { Events.Enqueue(e); return Task.CompletedTask; })
                .RunAsync(new BoxDeployWorkflow(input, Root, Executable), token);

        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
