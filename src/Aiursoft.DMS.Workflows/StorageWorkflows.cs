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
        await context.TraceAsync("Allocated directory bytes, one filesystem, no symlink traversal. Low CPU/IO priority; scan stops after 60 seconds. A failed or cancelled scan is partial, not a complete inventory.");
        return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow("/usr/bin/nice",
            ["-n", "19", "/usr/bin/ionice", "-c", "3", "/usr/bin/timeout", "--kill-after=5s", "60s", "/usr/bin/du",
                "--one-file-system", "--no-dereference", "--block-size=1", "--max-depth=" + value.Depth.ToString(CultureInfo.InvariantCulture), "--", value.Path]), token))!;
    }
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
