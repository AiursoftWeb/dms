using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed partial class BoxPullerForceUpdateWorkflow(PullerForceUpdateInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<PullerForceUpdateInput, PullerForceUpdateResult>(MachineWorkflows.PullerForceUpdate, input)
{
    // Arguments are passed separately. Only this compiled script can run inside the fixed Box service.
    private const string Sync = """
        set -euo pipefail
        entry=/app/entry.sh
        if [[ ! -x "$entry" || ! -r /app/source_updated.py ]] ||
            ! grep -Fq 'images=("$@")' "$entry" ||
            ! grep -Fq SKIP_IF_SYNCED_HOURS /app/source_updated.py; then
            echo 'This Puller does not support scoped force updates.' >&2
            exit 78
        fi
        for tool in flock pgrep; do
            if ! command -v "$tool" >/dev/null; then
                echo "Puller requires $tool for a guarded force update." >&2
                exit 78
            fi
        done
        exec 9>/app/dms-force-update.lock
        if ! flock --nonblock 9 || pgrep -f '^(/usr)?/bin/bash /app/entry[.]sh( |$)' >/dev/null; then
            echo 'Puller is already running. No new sync was started.' >&2
            exit 75
        fi
        echo "Starting a scoped Puller force update: $1"
        status=0
        "$entry" "$@" || status=$?
        if ((status != 0)); then
            echo "Puller finished with exit code $status." >&2
            exit 70
        fi
        echo 'Puller mirror sync completed. Service deployment must be checked separately.'
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
                ["exec", "--env", "SKIP_IF_SYNCED_HOURS=0", container, "/bin/bash", "-c", Sync, "dms-puller-force-update", input.Image]), token);
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
