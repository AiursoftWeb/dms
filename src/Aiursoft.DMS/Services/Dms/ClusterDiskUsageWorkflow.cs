using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed class ClusterDiskUsageWorkflow : ClusterWorkflow<EmptyInput, ClusterDiskUsageResult>
{
    public override async Task<ClusterDiskUsageResult> ExecuteAsync(WorkflowExecutionContext context, EmptyInput input, CancellationToken token)
    {
        long total = 0, used = 0, available = 0;
        var machines = new List<MachineDiskUsage>();
        foreach (var machine in await context.GetApprovedMachinesAsync(token))
        {
            var result = await context.CallMachineAsync(MachineWorkflows.DiskUsage, machine, input, token);
            checked { total += result.TotalBytes; used += result.UsedBytes; available += result.AvailableBytes; }
            machines.Add(new MachineDiskUsage(machine, result));
        }
        return new ClusterDiskUsageResult(total, used, available, machines.Count, machines);
    }
}
