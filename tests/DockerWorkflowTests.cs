using System.Collections.Concurrent;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class DockerWorkflowTests
{
    private const string Container = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    [DataRow("nginx")]
    [DataRow("0123456789ab")]
    [DataRow("--privileged")]
    [DataRow("*")]
    [DataRow("")]
    public void ContainerCommandsRequireAnImmutableFullId(string container)
    {
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerLogs.Serialize(new DockerLogsInput(container)));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(container, ["/start.sh"])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerKill.Serialize(new DockerContainerInput(container)));
    }

    [TestMethod]
    public void UnsafeOrUnboundedInputsAreRejectedBeforeSubmission()
    {
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerPs.Serialize(new DockerPsInput(GrepName: ".*")));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerLogs.Serialize(new DockerLogsInput(Container, 0)));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerLogs.Serialize(new DockerLogsInput(Container, 1001)));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(Container, [])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(Container, ["--privileged"])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(Container, ["echo", "a\0b"])));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(Container, ["echo", new string('a', 16385)])));
        Assert.Throws<JsonException>(() => MachineWorkflows.DockerPs.ReadInput("{\"Host\":\"tcp://remote:2375\"}"));
        Assert.Throws<JsonException>(() => MachineWorkflows.DockerExec.ReadInput($"{{\"Container\":\"{Container}\",\"Command\":[\"true\"],\"Privileged\":true}}"));
        Assert.Throws<ArgumentNullException>(() => MachineWorkflows.DockerKill.ReadInput("{\"Container\":null}"));
        Assert.Throws<ArgumentNullException>(() => MachineWorkflows.DockerExec.ReadInput($"{{\"Container\":\"{Container}\",\"Command\":null}}"));
        Assert.Throws<ArgumentNullException>(() => MachineWorkflows.DockerExec.ReadInput($"{{\"Container\":\"{Container}\",\"Command\":[\"echo\",null]}}"));
    }

    [TestMethod]
    public async Task PsUsesLiteralDockerNameFilteringAndFullIdsWithoutCommandOrEnvironmentOutput()
    {
        using var docker = new Docker();
        var result = MachineWorkflows.DockerPs.ReadResult(await docker.RunAsync(new DockerPsWorkflow(new DockerPsInput(true, "bot.worker-1"), docker.Executable)));
        Assert.AreEqual("container listing", result.Output.Trim());
        CollectionAssert.AreEqual(new[] { "--host", "unix:///var/run/docker.sock", "ps", "--no-trunc", "--format",
            "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.Status}}", "--all", "--filter", @"name=bot\.worker-1" }, docker.Arguments());
    }

    [TestMethod]
    public async Task LogsAreBoundedAndStreamBothOutputsIntoTheWorkflowTrace()
    {
        using var docker = new Docker();
        var since = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        await docker.RunAsync(new DockerLogsWorkflow(new DockerLogsInput(Container, 25, since), docker.Executable));
        CollectionAssert.AreEqual(new[] { "--host", "unix:///var/run/docker.sock", "logs", "--timestamps", "--tail", "25",
            "--since", "2026-11-15T10:00:00.0000000+00:00", Container }, docker.Arguments());
        Assert.IsTrue(docker.Events.Any(e => e.IsTrace && e.Source == "stdout" && e.Message.Contains("log line", StringComparison.Ordinal)));
        Assert.IsTrue(docker.Events.Any(e => e.IsTrace && e.Source == "stderr" && e.Message.Contains("error line", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExecPreservesArgumentsWithoutHostShellExpansionAndStreamsLargeOutput()
    {
        using var docker = new Docker();
        var command = new[] { "/bin/echo", "a b", "$(touch /never)", "; exit 7", "--server", "" };
        var result = MachineWorkflows.DockerExec.ReadResult(await docker.RunAsync(new DockerExecWorkflow(new DockerExecInput(Container, command), docker.Executable)));
        Assert.AreEqual(0, result.ExitCode);
        CollectionAssert.AreEqual(new[] { "--host", "unix:///var/run/docker.sock", "exec", Container }.Concat(command).ToArray(), docker.Arguments());
        Assert.IsGreaterThan(8192, docker.Events.Where(e => e.Source == "stdout").Sum(e => e.Message.Length));
        var child = docker.Events.Single(e => !e.IsTrace && e.Definition == WorkflowNames.Process && e.State == RunState.Succeeded);
        Assert.AreEqual(docker.Id, child.ParentId);
    }

    [TestMethod]
    public async Task KillOnlyTargetsTheApprovedContainer()
    {
        using var docker = new Docker();
        await docker.RunAsync(new DockerKillWorkflow(new DockerContainerInput(Container), docker.Executable));
        CollectionAssert.AreEqual(new[] { "--host", "unix:///var/run/docker.sock", "kill", Container }, docker.Arguments());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedMutationRequiresVerificationInsteadOfClaimingRemoteCancellation(bool kill)
    {
        using var docker = new Docker();
        File.WriteAllText(docker.PathOf("hold"), "");
        using var stop = new CancellationTokenSource();
        var run = docker.RunAsync(kill ? new DockerKillWorkflow(new DockerContainerInput(Container), docker.Executable)
            : new DockerExecWorkflow(new DockerExecInput(Container, ["/start.sh"]), docker.Executable), stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!File.Exists(docker.PathOf("arguments"))) await Task.Delay(20, timeout.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => run);
        Assert.AreEqual(RunState.NeedsVerification, docker.Events.Last(e => e.WorkflowId == docker.Id && !e.IsTrace).State);
    }

    [TestMethod]
    public async Task FailedExecPreservesExitEvidenceAndNeverReportsSuccess()
    {
        using var docker = new Docker();
        File.WriteAllText(docker.PathOf("fail"), "");
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => docker.RunAsync(new DockerExecWorkflow(new DockerExecInput(Container, ["false"]), docker.Executable)));
        Assert.IsTrue(docker.Events.Any(e => e.Definition == WorkflowNames.Process && e.State == RunState.Failed && e.Result.Contains("code 17", StringComparison.Ordinal)));
        Assert.IsFalse(docker.Events.Any(e => e.WorkflowId == docker.Id && e.State == RunState.Succeeded));
        await Assert.ThrowsAsync<ChildWorkflowException>(() => docker.RunAsync(new DockerPsWorkflow(new DockerPsInput(), docker.Executable)));
    }

    private sealed class Docker : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("dms-docker-").FullName;
        public Guid Id { get; } = Guid.NewGuid();
        public ConcurrentQueue<WorkflowEvent> Events { get; } = new();
        public string Executable => PathOf("docker");
        public string PathOf(string name) => Path.Combine(_directory, name);
        public string[] Arguments() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(PathOf("arguments")))!;

        public Docker()
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Docker workflows require Linux.");
            File.WriteAllText(Executable, """
                #!/usr/bin/python3
                import json, pathlib, sys, time
                directory = pathlib.Path(__file__).parent
                (directory / 'arguments').write_text(json.dumps(sys.argv[1:]))
                if (directory / 'hold').exists():
                    time.sleep(60)
                if (directory / 'fail').exists():
                    print('command or transport failure', file=sys.stderr)
                    sys.exit(17)
                operation = sys.argv[3]
                if operation == 'ps': print('container listing')
                if operation == 'logs':
                    print('log line')
                    print('error line', file=sys.stderr)
                if operation == 'exec': print('x' * 9000)
                if operation == 'kill': print(sys.argv[4])
                """);
            File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public Task<string> RunAsync(MachineWorkflow workflow, CancellationToken token = default) =>
            new MachineWorkflowContext(Id, null, e => { Events.Enqueue(e); return Task.CompletedTask; }).RunAsync(workflow, token);

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
