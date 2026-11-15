using System.Collections.Concurrent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class BoxPullerForceUpdateTests
{
    [TestMethod]
    [DataRow("aiursoft/dms")]
    [DataRow("aiursoft/dms:latest")]
    [DataRow("ghcr.io/goauthentik/proxy:2026.8.4")]
    public void AcceptsConcreteImages(string image) => MachineWorkflows.PullerForceUpdate.Serialize(new PullerForceUpdateInput(image));

    [TestMethod]
    [DataRow("")]
    [DataRow("--privileged")]
    [DataRow("aiursoft/dms;id")]
    [DataRow("aiursoft/dms\nredis")]
    [DataRow("https://hub.aiursoft.com/aiursoft/dms")]
    [DataRow("aiursoft/*")]
    [DataRow("aiursoft/dms latest")]
    public void RejectsCommandsOptionsUrlsAndBroadTargets(string image) =>
        Assert.Throws<ArgumentException>(() => MachineWorkflows.PullerForceUpdate.Serialize(new PullerForceUpdateInput(image)));

    [TestMethod]
    public void RejectsNullMissingOverlongAndExecutableOverrideInput()
    {
        Assert.Throws<ArgumentException>(() => MachineWorkflows.PullerForceUpdate.ReadInput("{\"Image\":null}"));
        Assert.Throws<System.Text.Json.JsonException>(() => MachineWorkflows.PullerForceUpdate.ReadInput("{}"));
        Assert.Throws<System.Text.Json.JsonException>(() => MachineWorkflows.PullerForceUpdate.ReadInput("{\"Image\":\"aiursoft/dms\",\"DockerExecutable\":\"/bin/bash\"}"));
        Assert.Throws<ArgumentException>(() => MachineWorkflows.PullerForceUpdate.Serialize(new PullerForceUpdateInput(new string('a', 256))));
    }

    [TestMethod]
    public async Task RunsOnlyApprovedImageUsingExistingPullerAndTracedSubworkflows()
    {
        using var fixture = new Puller();
        var result = MachineWorkflows.PullerForceUpdate.ReadResult(await fixture.RunAsync(CancellationToken.None));
        Assert.AreEqual("aiursoft/dms:latest", result.Image);
        Assert.AreEqual(Puller.Container, result.ContainerId);
        Assert.AreEqual("aiursoft/dms:latest|0", (await File.ReadAllTextAsync(fixture.PathOf("invocations"))).Trim());
        var events = fixture.Events.ToArray();
        var children = events.Where(e => e.State == RunState.Succeeded && e.Definition == WorkflowNames.Process && !e.IsTrace).ToArray();
        Assert.HasCount(2, children); Assert.AreNotEqual(children[0].WorkflowId, children[1].WorkflowId);
        Assert.IsTrue(children.All(e => e.ParentId == fixture.Id));
        Assert.IsTrue(events.Any(e => e.IsTrace && e.Message.Contains("Scoped mirror complete", StringComparison.Ordinal)));
        // More than the bounded query capture limit must still stream successfully.
        Assert.IsGreaterThan(8192, events.Where(e => e.IsTrace && e.Source == "stdout").Sum(e => e.Message.Length));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("invalid-container-id")]
    [DataRow(Puller.Container + "\n" + Puller.Container)]
    public async Task MissingAmbiguousOrInvalidContainerNeverStartsSync(string containers)
    {
        using var fixture = new Puller();
        await File.WriteAllTextAsync(fixture.PathOf("containers"), containers);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.RunAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.PathOf("invocations")));
    }

    [TestMethod]
    public async Task UnsupportedPullerAndConfirmedSyncFailureAreFailed()
    {
        using var fixture = new Puller();
        File.Delete(fixture.PathOf("app/source_updated.py"));
        await Assert.ThrowsAsync<ChildWorkflowException>(() => fixture.RunAsync(CancellationToken.None));
        Assert.IsFalse(File.Exists(fixture.PathOf("invocations")));
        Assert.AreEqual(RunState.Failed, fixture.Events.Last(e => !e.IsTrace && e.WorkflowId == fixture.Id).State);
        await File.WriteAllTextAsync(fixture.PathOf("app/source_updated.py"), "SKIP_IF_SYNCED_HOURS");
        await File.WriteAllTextAsync(fixture.PathOf("fail"), "");
        var failure = await Assert.ThrowsAsync<ChildWorkflowException>(() => fixture.RunAsync(CancellationToken.None));
        Assert.AreEqual("Process exited with code 70.", failure.Result);
        Assert.IsTrue(fixture.Events.Any(e => e.IsTrace && e.Message.Contains("exit code 9", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AlreadyRunningScheduledSyncPreventsAnotherInvocation()
    {
        using var fixture = new Puller();
        await File.WriteAllTextAsync(fixture.PathOf("hold"), "");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var scheduled = new System.Diagnostics.Process();
        scheduled.StartInfo = new System.Diagnostics.ProcessStartInfo(fixture.PathOf("app/entry.sh"))
        {
            UseShellExecute = false,
            ArgumentList = { "aiursoft/other:latest" },
            Environment = { ["SKIP_IF_SYNCED_HOURS"] = "1" }
        };
        scheduled.Start();
        try
        {
            while (!File.Exists(fixture.PathOf("invocations"))) await Task.Delay(30, stop.Token);
            var failure = await Assert.ThrowsAsync<ChildWorkflowException>(() => fixture.RunAsync(stop.Token));
            Assert.AreEqual("Process exited with code 75.", failure.Result);
            CollectionAssert.AreEqual(new[] { "aiursoft/other:latest|1" }, await File.ReadAllLinesAsync(fixture.PathOf("invocations")));
        }
        finally
        {
            if (!scheduled.HasExited) scheduled.Kill(entireProcessTree: true);
            await scheduled.WaitForExitAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task ConcurrentUpdateIsRejectedAndCancellationRequiresVerification()
    {
        using var fixture = new Puller();
        await File.WriteAllTextAsync(fixture.PathOf("hold"), "");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first = fixture.RunAsync(stop.Token);
        try
        {
            while (!File.Exists(fixture.PathOf("invocations"))) await Task.Delay(30, stop.Token);
            var second = await Assert.ThrowsAsync<ChildWorkflowException>(() => fixture.RunAsync(stop.Token));
            Assert.AreEqual("Process exited with code 75.", second.Result);
            Assert.HasCount(1, await File.ReadAllLinesAsync(fixture.PathOf("invocations")));
        }
        finally { await stop.CancelAsync(); }
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => first);
        Assert.AreEqual(RunState.NeedsVerification, fixture.Events.Last(e => !e.IsTrace && e.WorkflowId == fixture.Id).State);
    }

    [TestMethod]
    public async Task DockerTransportFailureDoesNotClaimTheRemoteCommandStopped()
    {
        using var fixture = new Puller();
        await File.WriteAllTextAsync(fixture.PathOf("disconnect"), "");
        await Assert.ThrowsAsync<WorkflowNeedsVerificationException>(() => fixture.RunAsync(CancellationToken.None));
        Assert.AreEqual(RunState.NeedsVerification, fixture.Events.Last(e => !e.IsTrace && e.WorkflowId == fixture.Id).State);
    }

    private sealed class Puller : IDisposable
    {
        public const string Container = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Format = "{{.ID}}";
        private readonly string _directory = Directory.CreateTempSubdirectory("dms-puller-").FullName;
        public Guid Id { get; } = Guid.NewGuid();
        public ConcurrentQueue<WorkflowEvent> Events { get; } = new();
        public string PathOf(string file) => Path.Combine(_directory, file);

        public Puller()
        {
            Directory.CreateDirectory(PathOf("app"));
            File.WriteAllText(PathOf("containers"), Container);
            File.WriteAllText(PathOf("app/source_updated.py"), "SKIP_IF_SYNCED_HOURS");
            WriteExecutable("app/entry.sh", $$"""
                #!/bin/bash
                set -eu
                images=("$@")
                directory='{{_directory}}'
                printf '%s|%s\n' "$1" "$SKIP_IF_SYNCED_HOURS" >> "$directory/invocations"
                if [[ -f "$directory/hold" ]]; then while true; do sleep 1; done; fi
                if [[ -f "$directory/fail" ]]; then exit 9; fi
                printf '%09000d\n' 0
                echo 'Scoped mirror complete'
                """);
            WriteExecutable("docker", $$"""
                #!/bin/bash
                set -eu
                directory='{{_directory}}'
                [[ "$1" == --host && "$2" == unix:///var/run/docker.sock ]]
                shift 2
                if [[ "$1" == ps ]]; then
                    [[ "$2" == --no-trunc && "$3" == --filter && "$4" == label=com.docker.swarm.service.name=puller_downloader && "$5" == --format && "$6" == '{{Format}}' ]]
                    cat "$directory/containers"
                    exit 0
                fi
                [[ "$#" == 8 && "$1" == exec && "$2" == --env && "$3" == SKIP_IF_SYNCED_HOURS=0 && "$4" == '{{Container}}' && "$5" == /usr/bin/python3 && "$6" == -c ]]
                if [[ -f "$directory/disconnect" ]]; then exit 125; fi
                export SKIP_IF_SYNCED_HOURS=0
                script="$7"
                script="${script//\/app\//$directory/app/}"
                exec /usr/bin/python3 -c "$script" "$8"
                """);
        }

        private void WriteExecutable(string name, string script)
        {
            var path = PathOf(name);
            File.WriteAllText(path, script);
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box workflows require Linux.");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public Task<string> RunAsync(CancellationToken token) => new MachineWorkflowContext(Id, null, e =>
        {
            Events.Enqueue(e); return Task.CompletedTask;
        }).RunAsync(new BoxPullerForceUpdateWorkflow(new PullerForceUpdateInput("aiursoft/dms:latest"), PathOf("docker")), token);

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
