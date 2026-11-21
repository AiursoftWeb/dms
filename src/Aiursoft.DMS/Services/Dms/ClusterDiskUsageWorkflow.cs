using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed class ClusterDiskUsageWorkflow : ClusterWorkflow<EmptyInput, DiskOverviewResult>
{
    public override async Task<DiskOverviewResult> ExecuteAsync(WorkflowExecutionContext context, EmptyInput input, CancellationToken token)
    {
        var probes = await context.ProbeMachinesAsync(MachineWorkflows.DiskUsage, input, token);
        var rows = probes.Select(probe => new DiskOverviewRow(probe.MachineId, probe.Name,
                probe.Status.ToString(), probe.WorkflowId, probe.Result?.TotalBytes, probe.Result?.UsedBytes,
                probe.Result?.AvailableBytes, probe.Result is { TotalBytes: > 0 } usage
                    ? Math.Round(100m * usage.UsedBytes / usage.TotalBytes, 1) : null))
            .OrderByDescending(row => row.UsedPercent).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.MachineId).ToList();
        var measured = rows.Where(row => row.TotalBytes != null).ToList();
        return new DiskOverviewResult(rows.Count, measured.Count,
            measured.Count == 0 ? null : measured.Sum(row => row.TotalBytes),
            measured.Count == 0 ? null : measured.Sum(row => row.UsedBytes),
            measured.Count == 0 ? null : measured.Sum(row => row.AvailableBytes), rows);
    }
}

public sealed class ClusterAgentHealthWorkflow : ClusterWorkflow<EmptyInput, List<AgentHealthRow>>
{
    public override async Task<List<AgentHealthRow>> ExecuteAsync(WorkflowExecutionContext context, EmptyInput input, CancellationToken token)
    {
        var probes = await context.ProbeMachinesAsync(MachineWorkflows.AgentStatus, input, token);
        return probes.Select(probe => new AgentHealthRow(probe.MachineId, probe.Name, probe.Online,
                probe.Status.ToString(), probe.WorkflowId, probe.Result?.Version, probe.Result?.ActiveTasks,
                probe.Result?.PendingEvents))
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.MachineId).ToList();
    }
}
