using System.CommandLine;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static ActionCommand HistorySourcesCommand() =>
        HistoryCommand("history-sources", OperationsWorkflows.PerformanceHistorySources);

    private static ActionCommand HistoryCommand(string name,
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
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        var after = new Option<string?>("--after")
            { Description = "Continue after NextCursor, keeping the same explicit window and filters." };
        if (resourceOption == null)
            return StandardCommand(name,
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
        var resource = new Option<string?>(resourceOption);
        return StandardCommand(name,
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
        var metrics = new Option<string[]>("--metric") { Required = true, AllowMultipleArgumentsPerToken = true };
        var instance = new Option<string?>("--instance");
        var after = new Option<string?>("--after");
        var since = new Option<DateTimeOffset?>("--since");
        var until = new Option<DateTimeOffset?>("--until");
        var interval = new Option<int>("--interval-seconds") { DefaultValueFactory = _ => 60 };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("pcp-history",
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
        var pid = new Option<int?>("--pid");
        var container = new Option<string?>("--container");
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 50 };
        return StandardCommand("blocked-processes",
            OperationsWorkflows.PerformanceBlockedProcesses,
            p => new BlockedProcessesInput(p.GetValue(pid), p.GetValue(container), p.GetValue(limit)),
            pid,
            container,
            limit);
    }

    private static ActionCommand KernelEventsCommand()
    {
        var since = new Option<DateTimeOffset?>("--since");
        var until = new Option<DateTimeOffset?>("--until");
        var category = new Option<string[]>("--category") { AllowMultipleArgumentsPerToken = true };
        var boot = new Option<string?>("--boot");
        var cursor = new Option<string?>("--before-cursor");
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 200 };
        return StandardCommand("kernel-events",
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
