using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed partial class BoxPullerForceUpdateWorkflow(PullerForceUpdateInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<PullerForceUpdateInput, PullerForceUpdateResult>(MachineWorkflows.PullerForceUpdate, input)
{
    // Reuse Puller's existing Python runtime. Request arguments never become script source.
    private const string Sync = """
        import fcntl, glob, os, subprocess, sys
        def fail(message, code):
            print(message, file=sys.stderr, flush=True)
            sys.exit(code)
        entry = '/app/entry.sh'
        try:
            with open(entry, encoding='utf-8') as source:
                scoped = 'images=("$@")' in source.read()
            with open('/app/source_updated.py', encoding='utf-8') as helper:
                scoped = scoped and 'SKIP_IF_SYNCED_HOURS' in helper.read()
        except OSError:
            scoped = False
        if not scoped or not os.access(entry, os.X_OK):
            fail('This Puller does not support scoped force updates.', 78)
        with open('/app/dms-force-update.lock', 'a') as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                fail('Puller is already running. No new sync was started.', 75)
            for process in glob.glob('/proc/[0-9]*/cmdline'):
                try:
                    with open(process, 'rb') as source:
                        args = source.read().split(b'\0')
                except OSError:
                    continue
                if len(args) > 1 and args[1] == os.fsencode(entry) and os.path.basename(args[0]) in (b'bash', b'sh'):
                    fail('Puller is already running. No new sync was started.', 75)
            print('Starting a scoped Puller force update: ' + sys.argv[1], flush=True)
            result = subprocess.run([entry, sys.argv[1]], check=False)
            if result.returncode != 0:
                fail('Puller finished with exit code ' + str(result.returncode) + '.', 70)
            print('Puller mirror sync completed. Service deployment must be checked separately.', flush=True)
        """;

    public override async Task<PullerForceUpdateResult> ExecuteAsync(MachineWorkflowContext context, PullerForceUpdateInput input, CancellationToken token)
    {
        input.Validate();
        var discovery = JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(dockerExecutable,
            ["ps", "--no-trunc", "--filter", "label=com.docker.swarm.service.name=puller_downloader", "--format", "{{.ID}}"], captureOutput: true), token))!;
        var containers = discovery.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (containers.Length != 1 || !ContainerId().IsMatch(containers[0]))
            throw new InvalidOperationException("Expected exactly one running local puller_downloader container. No sync was started.");
        var container = containers[0];
        await context.TraceAsync($"Forcing the existing Box Puller to check {input.Image}; container {container}. This does not force a service deployment.");
        token.ThrowIfCancellationRequested();
        try
        {
            await context.CallAsync(new ProcessWorkflow(dockerExecutable,
                ["exec", "--env", "SKIP_IF_SYNCED_HOURS=0", container, "/usr/bin/python3", "-c", Sync, input.Image]), token);
        }
        catch (ChildWorkflowException exception) when (exception.Result is "Process exited with code 70." or "Process exited with code 75." or "Process exited with code 78.")
        {
            throw; // The fixed wrapper positively reported failure, busy, or unsupported Puller.
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChildWorkflowException)
        {
            // Killing a Docker client does not prove that its server-side exec stopped.
            await context.TraceAsync("The Docker exec outcome is unknown. Puller may still be running; verify before submitting another update.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        return new PullerForceUpdateResult(input.Image, container);
    }

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex ContainerId();
}
