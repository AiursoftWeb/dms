using System.CommandLine;
using Aiursoft.CommandFramework;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

public static class DmsCommandApp
{
    public static NestedCommandApp Create() => new NestedCommandApp()
        .WithFeature(new LoginCommand())
        .WithFeature(new Group("cluster", [new ManagementCommand("list", "clusters"), new ManagementCommand("create", "create"), new ManagementCommand("rename", "rename"), new ManagementCommand("drop", "drop"), StandardCommand("get-machines", ClusterWorkflows.GetMachines, _ => new EmptyInput()), new ActionCommand("add-machine", WorkflowNames.AddMachine),
            new Group("get", [StandardCommand("disk-usage", ClusterWorkflows.DiskUsage, _ => new EmptyInput())])]))
        .WithFeature(new Group("access", [new ManagementCommand("whoami", "identity"), new ManagementCommand("get", "access"), new ManagementCommand("issue-agent-key", "key"), new ManagementCommand("create-join-key", "join-create"), new ManagementCommand("list-join-keys", "join-list"), new ManagementCommand("revoke-join-key", "join-revoke"), new ManagementCommand("list-enrollments", "enrollments")]))
        .WithFeature(new Group("machine", [
            new Group("docker", [DockerPsCommand(), DockerLogsCommand(), DockerExecCommand(), DockerKillCommand()]),
            new Group("box", [new Group("puller", [PullerCommand()])]),
            new Group("performance", [StandardCommand("info", MachineWorkflows.Performance, _ => new EmptyInput())]),
            new Group("systemctl", [ServiceCommand("restart", MachineWorkflows.Restart), ServiceCommand("status", MachineWorkflows.ServiceStatus)]),
            new Group("agent", [StandardCommand("status", MachineWorkflows.AgentStatus, _ => new EmptyInput()), new ActionCommand("upgrade", WorkflowNames.AgentUpgrade, MachineWorkflows.AgentUpgrade)]),
            new Group("layout", [StandardCommand("lsblk", MachineWorkflows.ListBlockDevices, _ => new EmptyInput()), StandardCommand("df", MachineWorkflows.DiskFree, _ => new EmptyInput())])]))
        .WithFeature(new Group("lockbox", [new ActionCommand("get", "get"), new ActionCommand("approve", "approve"), new ActionCommand("deny", "deny")]))
        .WithFeature(new Group("operation", [new ActionCommand("list-definitions", "definitions"), new ActionCommand("query-audit", "audit"), new ActionCommand("get-workflow", "workflow"), new ActionCommand("get-callstack", "callstack"), new ActionCommand("get-trace", "trace"), new ActionCommand("kill-workflow", WorkflowNames.Kill)]));

    private static ActionCommand StandardCommand<TInput, TResult>(string name, WorkflowDefinition<TInput, TResult> definition,
        Func<ParseResult, TInput> input, params Option[] options) =>
        new(name, definition.Name, definition, context => definition.Serialize(input(context)), options);

    private static ActionCommand PullerCommand()
    {
        var image = new Option<string?>("--image") { Description = "One image; omit to refresh all images configured in the selected Puller." };
        var puller = new Option<string>("--puller") { Description = "Puller to update: main (default) or cn." };
        return StandardCommand("force-update", MachineWorkflows.PullerForceUpdate,
            context => new PullerForceUpdateInput(context.GetValue(image), context.GetValue(puller) ?? "main"), image, puller);
    }

    private static ActionCommand DockerPsCommand()
    {
        var all = new Option<bool>("--all") { Description = "Include stopped containers." };
        var name = new Option<string?>("--grep-name") { Description = "Only containers whose names contain this literal text." };
        return StandardCommand("ps", MachineWorkflows.DockerPs,
            context => new DockerPsInput(context.GetValue(all), context.GetValue(name)), all, name);
    }

    private static Option<string> ContainerOption() => new("--container")
    {
        Required = true, Description = "Full container ID from docker ps; names and short IDs are not accepted."
    };

    private static ActionCommand DockerLogsCommand()
    {
        var container = ContainerOption();
        var tail = new Option<int>("--tail") { DefaultValueFactory = _ => 100, Description = "Last 1 to 1000 lines (default 100)." };
        var since = new Option<DateTimeOffset?>("--since") { Description = "Only logs since this timestamp with UTC offset." };
        return StandardCommand("logs", MachineWorkflows.DockerLogs,
            context => new DockerLogsInput(context.GetValue(container)!, context.GetValue(tail), context.GetValue(since)), container, tail, since);
    }

    private static ActionCommand DockerExecCommand()
    {
        var container = ContainerOption();
        var command = new Argument<string[]>("command") { Arity = ArgumentArity.OneOrMore, Description = "Executable and arguments after --; no implicit shell." };
        return new ActionCommand("exec", MachineWorkflows.DockerExec.Name, MachineWorkflows.DockerExec,
            context => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(context.GetValue(container)!, context.GetValue(command)!)), [container], command);
    }

    private static ActionCommand DockerKillCommand()
    {
        var container = ContainerOption();
        return StandardCommand("kill", MachineWorkflows.DockerKill,
            context => new DockerContainerInput(context.GetValue(container)!), container);
    }

    private static ActionCommand ServiceCommand(string name, MachineWorkflowDefinition<ServiceInput, ProcessResult> definition)
    {
        var service = new Option<string>("--service") { Required = true };
        return StandardCommand(name, definition, context => new ServiceInput(context.GetValue(service) ?? string.Empty), service);
    }

    private sealed class Group(string name, CommandHandlerBuilder[] children) : NavigationCommandHandlerBuilder
    {
        protected override string Name => name;
        protected override string Description => $"Manage {name}.";
        protected override CommandHandlerBuilder[] GetSubCommandHandlers() => children;
    }

    private sealed class ActionCommand(string name, string action, WorkflowDefinition? definition = null, Func<ParseResult, string>? input = null, Option[]? options = null, Argument? argument = null) : DmsCommandHandler
    {
        public override Command BuildAsCommand()
        {
            var command = base.BuildAsCommand();
            if (argument != null) command.Arguments.Add(argument);
            return command;
        }
        private bool IsSubmission => definition != null || action is WorkflowNames.AddMachine or WorkflowNames.Kill;
        protected override string Name => name;
        protected override string Description => IsSubmission ? "Submit a Lockbox request. Execution starts only after approval." : "Inspect or decide an existing workflow request.";
        private readonly Option<Guid> _cluster = new("--cluster-id") { Description = "Cluster ID; may be omitted when exactly one cluster exists." };
        private readonly Option<Guid[]> _machines = new("--machine-id") { AllowMultipleArgumentsPerToken = true, Description = "Explicit machines; may be omitted when the cluster has exactly one active machine." };
        private readonly Option<bool> _all = new("--all-machines");
        private readonly Option<Guid> _id = new("--id");
        private readonly Option<Guid> _request = new("--request-id") { Description = "Reuse this ID only to retry the same submission." };
        private readonly Option<string> _reason = new("--reason");
        private readonly Option<string> _machineName = new("--name");
        private readonly Option<int> _skip = new("--skip");
        private readonly Option<DateTimeOffset?> _from = new("--from") { Description = "Inclusive archive recording time, ISO 8601 with UTC offset; defaults to 24 hours before --until." };
        private readonly Option<DateTimeOffset?> _until = new("--until") { Description = "Exclusive archive recording time, ISO 8601 with UTC offset; defaults to now. Maximum range: 31 days." };
        private readonly Option<Guid?> _machineFilter = new("--machine-id");
        private readonly Option<Guid?> _workflowFilter = new("--workflow-id");
        private readonly Option<Guid?> _rootFilter = new("--root-workflow-id");
        private readonly Option<Guid?> _lockboxFilter = new("--lockbox-id");
        private readonly Option<string?> _requester = new("--requester-id");
        private readonly Option<string?> _approver = new("--approver-id");
        private readonly Option<string?> _definitionFilter = new("--definition");
        private readonly Option<string?> _requestedDefinition = new("--requested-definition");
        private readonly Option<WorkflowAuditKind?> _kind = new("--kind");
        private readonly Option<RunState?> _state = new("--state");
        private readonly Option<bool?> _automatic = new("--automatic-approval");
        private readonly Option<int> _page = new("--page") { Description = "Zero-based archive page, 50 events per page." };

        protected override IEnumerable<Option> GetActionOptions()
        {
            if (IsSubmission)
            {
                _reason.Required = true;
                yield return _cluster; yield return _request; yield return _reason;
                if (definition?.RequiresTargets == true) { yield return _machines; yield return _all; }
                foreach (var option in options ?? []) yield return option;
                if (action == WorkflowNames.AddMachine) { _machineName.Required = true; yield return _machineName; }
                if (action == WorkflowNames.Kill) { _id.Required = true; yield return _id; }
            }
            else if (action == "definitions")
            {
                _cluster.Required = true; yield return _cluster;
            }
            else if (action == "audit")
            {
                _cluster.Required = true; yield return _cluster;
                foreach (var option in new Option[] { _from, _until, _machineFilter, _workflowFilter, _rootFilter, _lockboxFilter,
                    _requester, _approver, _definitionFilter, _requestedDefinition, _kind, _state, _automatic, _page }) yield return option;
            }
            else
            {
                _id.Required = true; yield return _id;
                if (action is "approve" or "deny") yield return _reason;
                if (action == "trace") yield return _skip;
            }
        }

        protected override bool Callstack => action == "callstack";
        protected override bool Catalog => action == "definitions";
        protected override bool Audit => action == "audit";

        protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
        {
            var id = context.GetValue(_id);
            DmsResponse response;
            if (IsSubmission)
            {
                var requestId = context.GetValue(_request);
                response = await client.SubmitAsync(new OperationRequest
                {
                    RequestId = requestId == Guid.Empty ? Guid.NewGuid() : requestId,
                    ClusterId = context.GetValue(_cluster), Workflow = action, MachineIds = (context.GetValue(_machines) ?? []).ToList(),
                    AllMachines = context.GetValue(_all), Reason = context.GetValue(_reason) ?? string.Empty,
                    Input = action == WorkflowNames.AgentUpgrade ? MachineWorkflows.AgentUpgrade.Serialize(new AgentUpgradeInput((await client.AgentReleasesAsync()).Data)) : input?.Invoke(context) ?? "{}",
                    MachineName = context.GetValue(_machineName) ?? string.Empty,
                    TargetWorkflowId = action == WorkflowNames.Kill ? id : null
                });
            }
            else response = action switch
            {
                "audit" => await client.AuditEventsAsync(new WorkflowAuditQuery
                {
                    ClusterId = context.GetValue(_cluster), From = context.GetValue(_from), Until = context.GetValue(_until),
                    MachineId = context.GetValue(_machineFilter), WorkflowId = context.GetValue(_workflowFilter),
                    RootWorkflowId = context.GetValue(_rootFilter), LockboxId = context.GetValue(_lockboxFilter),
                    RequesterId = context.GetValue(_requester), ApproverId = context.GetValue(_approver),
                    Definition = context.GetValue(_definitionFilter), RequestedDefinition = context.GetValue(_requestedDefinition),
                    Kind = context.GetValue(_kind), State = context.GetValue(_state), AutomaticApproval = context.GetValue(_automatic), Page = context.GetValue(_page)
                }),
                "definitions" => await client.ListDefinitionsAsync(context.GetValue(_cluster)),
                "get" => await client.GetLockboxAsync(id),
                "approve" or "deny" => await client.DecideAsync(id, action == "approve", context.GetValue(_reason) ?? string.Empty),
                "workflow" => await client.GetWorkflowAsync(id),
                "callstack" => await client.CallstackAsync(id),
                "trace" => await client.TraceAsync(id, context.GetValue(_skip)),
                _ => throw new InvalidOperationException("Unsupported command.")
            };
            return response;
        }
    }
}
