using System.Collections.Concurrent;
using System.Diagnostics;
using Aiursoft.DMS.Client;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class BoxDeployStackTests
{
    private sealed class TempBox : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("dms-box-stack-").FullName;
        public string Source { get; }
        public string Repository { get; }

        public TempBox()
        {
            Source = Path.Combine(Root, "origin.git");
            var work = Path.Combine(Root, "seed");
            Repository = Path.Combine(Root, "boxforproart");
            Directory.CreateDirectory(Source);
            Directory.CreateDirectory(work);

            RunGit(Source, "init", "--bare", "--initial-branch=master");
            RunGit(work, "init", "--initial-branch=master");
            RunGit(work, "config", "user.name", "Test");
            RunGit(work, "config", "user.email", "test@example.test");
            RunGit(work, "config", "commit.gpgsign", "false");

            File.WriteAllText(Path.Combine(work, "deploy.sh"), "#!/bin/bash\nexit 0\n");
            File.WriteAllText(Path.Combine(work, ".env"), "REGISTRY_HOST=hub.example.test\nLOG_LEVEL=info\n");
            Directory.CreateDirectory(Path.Combine(work, "stage4", "stacks", "prbot"));
            File.WriteAllText(Path.Combine(work, "stage4", "stacks", "prbot", "docker-compose.yml"),
                "services:\n  app:\n    image: {{REGISTRY_HOST}}/aiursoft/prbot:latest\n    environment:\n      - LOG_LEVEL={{LOG_LEVEL}}\n      - API_SECRET={{PRBOT_SECRET}}\n      - HOST={{.Node.Hostname}}\n");

            RunGit(work, "add", ".");
            RunGit(work, "commit", "-m", "initial");
            RunGit(work, "remote", "add", "origin", Source);
            RunGit(work, "push", "origin", "master");

            RunGit(Root, "clone", Source, Repository);
            RunGit(Repository, "remote", "set-url", "origin", "https://gitlab.aiursoft.com/aiursoft/boxforproart.git");
            // Redirect official URL back to local bare repo via git config insteadOf? No, Preflight checks remote.origin.url!
            // Instead configure url.<Source>.insteadOf so fetch hits local bare repo while origin URL stays official.
            RunGit(Repository, "config", $"url.{Source}.insteadOf", "https://gitlab.aiursoft.com/aiursoft/boxforproart.git");
            File.WriteAllText(Path.Combine(Repository, ".env.prod"), "LOG_LEVEL=debug\nPRBOT_SECRET=UltraSecretToken9876\n");
        }

        public void AdvanceStackCompose(string newCompose)
        {
            var work = Path.Combine(Root, "seed");
            File.WriteAllText(Path.Combine(work, "stage4", "stacks", "prbot", "docker-compose.yml"), newCompose);
            RunGit(work, "add", ".");
            RunGit(work, "commit", "-m", "update stack");
            RunGit(work, "push", "origin", "master");
        }

        private static void RunGit(string cwd, params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/git")
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void ContractEnforcesMediumRiskAndValidStackNames()
    {
        Assert.AreEqual(RiskLevel.Medium, MachineWorkflows.BoxDeployStack.Risk);
        Assert.IsTrue(MachineWorkflows.BoxDeployStack.PublicEntry);
        Assert.IsTrue(new MachineWorkflowRegistry().Contains(MachineWorkflows.BoxDeployStack.Name));
        foreach (var bad in new[] { "", "..", "../etc", "a/b", "a b", "a;id", "-flag" })
            Assert.Throws<ArgumentException>(() => MachineWorkflows.BoxDeployStack.Serialize(new BoxDeployStackInput(bad)));
    }

    [TestMethod]
    public void RenderComposeSubstitutesVariablesPreservesGoTemplatesAndFailsOnMissingKeys()
    {
        var template = "image: {{REGISTRY}}/app\ntoken: {{SECRET}}\nnode: {{.Node.Hostname}}";
        var rendered = BoxDeployStackWorkflow.RenderCompose(template, new Dictionary<string, string>
        {
            ["REGISTRY"] = "hub.test",
            ["SECRET"] = "s3cr3t/with&special"
        }, "demo");
        Assert.AreEqual("image: hub.test/app\ntoken: s3cr3t/with&special\nnode: {{.Node.Hostname}}", rendered);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            BoxDeployStackWorkflow.RenderCompose(template, new Dictionary<string, string> { ["REGISTRY"] = "hub.test" }, "demo"));
        StringAssert.Contains(ex.Message, "SECRET");
        Assert.DoesNotContain("hub.test", ex.Message);
    }

    [TestMethod]
    public async Task DeployStackPullsRendersInMemoryAndScrubsSecretsFromTrace()
    {
        using var box = new TempBox();
        box.AdvanceStackCompose(
            "services:\n  app:\n    image: {{REGISTRY_HOST}}/aiursoft/prbot:v2\n    environment:\n      - LOG_LEVEL={{LOG_LEVEL}}\n      - API_SECRET={{PRBOT_SECRET}}\n      - NODE={{.Node.Hostname}}\n");

        NativeCommand? capturedDocker = null;
        var host = new OperationHost(native: (cmd, _) =>
        {
            capturedDocker = cmd;
            // Simulate docker echoing a line that accidentally contains the secret -> must be scrubbed!
            return Task.FromResult(new NativeResult(0, "Updating service prbot_app (debug UltraSecretToken9876)\n", ""));
        });
        var agent = new AgentEnvironment(Guid.NewGuid(), "1.0.0", 0, 0, Operations: host);
        var events = new ConcurrentQueue<WorkflowEvent>();
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { events.Enqueue(e); return Task.CompletedTask; }, agent, new MachineWorkflowRegistry());
        var workflow = new BoxDeployStackWorkflow(new BoxDeployStackInput("prbot", Pull: true), agent, root: box.Root, gitConfigurationPath: Path.Combine(box.Root, "no-git-cfg"));

        var raw = await context.RunAsync(workflow, CancellationToken.None);
        var result = MachineWorkflows.BoxDeployStack.ReadResult(raw);

        Assert.AreEqual(box.Repository, result.Repository);
        Assert.AreEqual("prbot", result.Stack);
        Assert.AreEqual("stage4/stacks/prbot/docker-compose.yml", result.ComposeFile);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(result.HealthVerified);
        Assert.AreNotEqual(result.PreviousCommit, result.Commit);
        Assert.IsNotNull(capturedDocker);
        Assert.AreEqual("docker", capturedDocker.Executable);
        CollectionAssert.Contains(capturedDocker.Arguments.ToArray(), "stack");
        CollectionAssert.Contains(capturedDocker.Arguments.ToArray(), "deploy");
        CollectionAssert.Contains(capturedDocker.Arguments.ToArray(), "--prune");
        CollectionAssert.Contains(capturedDocker.Arguments.ToArray(), "--with-registry-auth");
        CollectionAssert.Contains(capturedDocker.Arguments.ToArray(), "prbot");
        Assert.AreEqual(Path.Combine(box.Repository, "stage4", "stacks", "prbot"), capturedDocker.WorkingDirectory);
        StringAssert.Contains(capturedDocker.StandardInput!, "hub.example.test/aiursoft/prbot:v2");
        StringAssert.Contains(capturedDocker.StandardInput!, "LOG_LEVEL=debug");
        StringAssert.Contains(capturedDocker.StandardInput!, "API_SECRET=UltraSecretToken9876");
        StringAssert.Contains(capturedDocker.StandardInput!, "{{.Node.Hostname}}");

        var trace = string.Join('\n', events.Where(e => e.IsTrace).Select(e => e.Message));
        Assert.DoesNotContain("UltraSecretToken9876", trace);
        StringAssert.Contains(trace, "[redacted]");
    }

    [TestMethod]
    public async Task DeployStackWithoutPullSkipsFetchAndRejectsSymlinkCompose()
    {
        using var box = new TempBox();
        var dockerCalled = false;
        var host = new OperationHost(native: (_, _) =>
        {
            dockerCalled = true;
            return Task.FromResult(new NativeResult(0, "ok\n", ""));
        });
        var agent = new AgentEnvironment(Guid.NewGuid(), "1.0.0", 0, 0, Operations: host);
        var context = new MachineWorkflowContext(Guid.NewGuid(), null, _ => Task.CompletedTask, agent, new MachineWorkflowRegistry());

        // Succeeds with Pull: false
        var wf = new BoxDeployStackWorkflow(new BoxDeployStackInput("prbot", Pull: false), agent, root: box.Root, gitConfigurationPath: Path.Combine(box.Root, "no-git-cfg"));
        var res = MachineWorkflows.BoxDeployStack.ReadResult(await context.RunAsync(wf, CancellationToken.None));
        Assert.AreEqual(res.PreviousCommit, res.Commit);
        Assert.IsTrue(dockerCalled);

        // Replace compose with symlink -> refused before docker
        dockerCalled = false;
        var compose = Path.Combine(box.Repository, "stage4", "stacks", "prbot", "docker-compose.yml");
        var outside = Path.Combine(box.Root, "outside.yml");
        File.WriteAllText(outside, "services: {}\n");
        File.Delete(compose);
        File.CreateSymbolicLink(compose, outside);
        var repo = box.Repository;
        Assert.Throws<IOException>(() => BoxDeployStackWorkflow.ResolveComposePath(repo, "prbot"));
        Assert.IsFalse(dockerCalled);
    }

    [TestMethod]
    public async Task BoxDeployStackCliHelpExposesStackAndNoPullOptions()
    {
        var help = await DmsCommandApp.Create().TestRunAsync(["machine", "box", "deploy-stack", "--help"]);
        Assert.AreEqual(0, help.ProgramReturn, help.StdErr);
        StringAssert.Contains(help.StdOut, "--stack");
        StringAssert.Contains(help.StdOut, "--no-pull");
        Assert.DoesNotContain("[default: true]", help.StdOut);
    }
}
