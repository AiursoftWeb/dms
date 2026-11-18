using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class DirectoryUsageWorkflow(DirectoryUsageInput input)
    : MachineWorkflow<DirectoryUsageInput, ProcessResult>(MachineWorkflows.DirectoryUsage, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, DirectoryUsageInput value, CancellationToken token)
    {
        value.Validate();
        await context.TraceAsync($"Directory usage scan: path={JsonSerializer.Serialize(value.Path)}; max_depth={value.Depth}; timeout_seconds=60. Allocated bytes, one filesystem, no symlink traversal, low CPU/IO priority. Other filesystems/subvolumes may be excluded; du and df measure different things.");
        try
        {
            var result = JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow("/usr/bin/nice",
                ["-n", "19", "/usr/bin/ionice", "-c", "3", "/usr/bin/timeout", "--kill-after=5s", "60s", "/usr/bin/du",
                    "--one-file-system", "--no-dereference", "--block-size=1", "--max-depth=" + value.Depth.ToString(CultureInfo.InvariantCulture), "--", value.Path]), token))!;
            await context.TraceAsync("Directory usage scan completed within its configured scope; byte counts are in the process subworkflow trace.");
            return result;
        }
        catch (ChildWorkflowException exception)
        {
            await context.TraceAsync($"Directory usage scan failed. Any emitted byte counts and native errors remain in subworkflow {exception.WorkflowId}. Exit code 124 means the 60-second scan limit was reached; other errors can identify inaccessible mounts or files removed during a live scan. Inspect stderr and retry a narrower --path; do not infer a complete disk inventory from partial output.", "error");
            throw;
        }
        catch (OperationCanceledException)
        {
            await context.TraceAsync("Directory usage scan was cancelled. Output already emitted by the process subworkflow is partial; it does not establish a complete disk inventory.", "error");
            throw;
        }
    }
}

internal static class FilesystemUsageDiagnostics
{
    public const string Scope = "Collecting local filesystem usage. Several mount rows may refer to the same filesystem (including Btrfs subvolumes); do not add those rows as independent disk capacity.";
    public const string Failure = "df reports overall failure when any inspected mount cannot be read, even if other filesystem rows were emitted. Inspect stderr for the affected mount (for example a disconnected FUSE mount). Retain emitted rows as partial evidence for the filesystems they name; they do not establish that every mount was readable.";
}

public sealed class DockerDiskUsageWorkflow(string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<EmptyInput, ProcessResult>(MachineWorkflows.DockerDiskUsage, new EmptyInput())
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput value, CancellationToken token)
    {
        await context.TraceAsync("Docker reports estimated reclaimable space. This is an inspection only; no images, containers, volumes or build cache are removed.");
        return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(dockerExecutable,
            ["--host", "unix:///var/run/docker.sock", "system", "df", "--verbose"]), token))!;
    }
}
