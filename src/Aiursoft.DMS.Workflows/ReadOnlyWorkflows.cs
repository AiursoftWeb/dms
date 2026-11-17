using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class AgentStatusWorkflow(Guid installationId, string version, int activeTasks, int pendingEvents) : MachineWorkflow
{
    public override string Name => WorkflowNames.AgentStatus;
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = JsonSerializer.Serialize(new { InstallationId = installationId, Version = version, ActiveTasks = activeTasks, PendingEvents = pendingEvents });
        await context.TraceAsync(result);
        return result;
    }
}

public sealed class ServiceStatusWorkflow(string service) : MachineWorkflow
{
    public override string Name => WorkflowNames.ServiceStatus;
    public override string Input => JsonSerializer.Serialize(new ServiceInput(service));
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        if (!RestartServiceWorkflow.ValidService(service)) throw new ArgumentException("A concrete .service unit name is required.");
        var result = await context.CallAsync(new ProcessWorkflow("/usr/bin/systemctl",
            ["show", "--property=Id,LoadState,ActiveState,SubState,UnitFileState", "--", service], captureOutput: true), token);
        if (JsonSerializer.Deserialize<ProcessResult>(result)!.Output.Split('\n').Contains("LoadState=not-found"))
            throw new InvalidOperationException("The requested service unit was not found.");
        return result;
    }
}

public sealed class ListBlockDevicesWorkflow : MachineWorkflow
{
    public override string Name => WorkflowNames.ListBlockDevices;
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        var result = await context.CallAsync(new ProcessWorkflow("/usr/bin/lsblk",
            ["--json", "--bytes", "--exclude", "7", "--output", "NAME,TYPE,SIZE,FSTYPE,MOUNTPOINT"], captureOutput: true), token);
        // Validate the fixed command's structured payload before publishing it as a result.
        using var document = JsonDocument.Parse(JsonSerializer.Deserialize<ProcessResult>(result)!.Output);
        return result;
    }
}

public sealed class DiskFreeWorkflow : MachineWorkflow
{
    public override string Name => WorkflowNames.DiskFree;
    public override Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token) =>
        context.CallAsync(new ProcessWorkflow("/usr/bin/df",
            ["--block-size=1", "--output=source,fstype,size,used,avail,pcent,target", "--local",
                "--exclude-type=overlay", "--exclude-type=tmpfs", "--exclude-type=devtmpfs", "--exclude-type=squashfs", "--exclude-type=efivarfs"]), token);
}
