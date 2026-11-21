using System.CommandLine;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static ActionCommand HistorySourcesCommand() =>
        HistoryCommand("history-sources",
            "Find historical performance data and archive IDs.",
            OperationsWorkflows.PerformanceHistorySources);

    private static ActionCommand HistoryCommand(string name,
        string description,
        MachineWorkflowDefinition<HistoryInput, OperationResult> definition,
        string? resourceOption = null)
    {
        var since = new Option<DateTimeOffset?>("--since")
            { Description = "Start with explicit UTC offset; default is one hour before until." };
        var until = new Option<DateTimeOffset?>("--until")
            { Description = "End with explicit UTC offset; default is execution time." };
        var interval = new Option<int>("--interval-seconds")
        {
            DefaultValueFactory = _ => 60,
            Description = "Select the first original sample per interval bucket; does not interpolate."
        };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum historical samples to return." };
        var after = new Option<string?>("--after")
            { Description = "Continue after NextCursor, keeping the same explicit window and filters." };
        if (resourceOption == null)
            return StandardCommand(name,
                description,
                definition,
                p => new HistoryInput(p.GetValue(since),
                    p.GetValue(until),
                    p.GetValue(interval),
                    p.GetValue(limit),
                    After: p.GetValue(after)),
                since,
                until,
                interval,
                limit,
                after);
        var resource = new Option<string?>(resourceOption)
        {
            Description = resourceOption switch
            {
                "--cpu" => "Filter to one CPU label, such as cpu0.",
                "--device" => "Filter to one block device, such as sda.",
                _ => "Filter to one network interface, such as eth0."
            }
        };
        return StandardCommand(name,
            description,
            definition,
            p => new HistoryInput(p.GetValue(since),
                p.GetValue(until),
                p.GetValue(interval),
                p.GetValue(limit),
                p.GetValue(resource),
                p.GetValue(after)),
            since,
            until,
            interval,
            limit,
            resource,
            after);
    }

    private static ActionCommand PcpHistoryCommand()
    {
        var archive = new Option<string>("--archive-id")
            { Required = true, Description = "ID returned by history-sources on this machine." };
        var metrics = new Option<string[]>("--metric")
        {
            Required = true, AllowMultipleArgumentsPerToken = true,
            Description = "Literal PCP metric names, such as kernel.all.load."
        };
        var instance = new Option<string?>("--instance") { Description = "Read one named PCP metric instance." };
        var after = new Option<string?>("--after")
            { Description = "Continue from the returned NextCursor with the same filters and time window." };
        var since = new Option<DateTimeOffset?>("--since")
            { Description = "Beginning of the time window in ISO 8601 with a UTC offset." };
        var until = new Option<DateTimeOffset?>("--until")
            { Description = "End of the time window in ISO 8601 with a UTC offset." };
        var interval = new Option<int>("--interval-seconds")
            { DefaultValueFactory = _ => 60, Description = "Seconds per sample bucket." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of sample rows to return." };
        return StandardCommand("pcp-history",
            "Read selected metrics from a Performance Co-Pilot archive.",
            OperationsWorkflows.PerformancePcpHistory,
            p => new PcpHistoryInput(p.GetValue(archive)!,
                p.GetValue(metrics)!,
                p.GetValue(since),
                p.GetValue(until),
                p.GetValue(interval),
                p.GetValue(limit),
                p.GetValue(instance),
                p.GetValue(after)),
            archive,
            metrics,
            instance,
            since,
            until,
            interval,
            limit,
            after);
    }

    private static ActionCommand BlockedProcessesCommand()
    {
        var pid = new Option<int?>("--pid") { Description = "Filter to one process ID." };
        var container = new Option<string?>("--container")
            { Description = "Full Docker container ID from dms machine docker ps." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 50, Description = "Maximum number of blocked processes to return." };
        return StandardCommand("blocked-processes",
            "Find processes blocked in uninterruptible sleep.",
            OperationsWorkflows.PerformanceBlockedProcesses,
            p => new BlockedProcessesInput(p.GetValue(pid), p.GetValue(container), p.GetValue(limit)),
            pid,
            container,
            limit);
    }

    private static ActionCommand KernelEventsCommand()
    {
        var since = new Option<DateTimeOffset?>("--since")
            { Description = "Beginning of the time window in ISO 8601 with a UTC offset." };
        var until = new Option<DateTimeOffset?>("--until")
            { Description = "End of the time window in ISO 8601 with a UTC offset." };
        var category = new Option<string[]>("--category")
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "Filter by oom, hung-task, io-error, filesystem, or reset."
        };
        var boot = new Option<string?>("--boot")
            { Description = "Use 0 for the current boot, -1 for the previous boot, or a full boot ID." };
        var cursor = new Option<string?>("--before-cursor")
            { Description = "Read older events before the given journal cursor." };
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => 200, Description = "Maximum number of kernel events to return." };
        return StandardCommand("kernel-events",
            "Read kernel events with time, boot, and category filters.",
            OperationsWorkflows.SystemKernelEvents,
            p => new KernelEventsInput(p.GetValue(since),
                p.GetValue(until),
                p.GetValue(limit),
                p.GetValue(category) is { Length: > 0 } items ? items : null,
                p.GetValue(boot),
                p.GetValue(cursor)),
            since,
            until,
            category,
            boot,
            cursor,
            limit);
    }
}
