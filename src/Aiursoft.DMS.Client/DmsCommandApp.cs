using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using Aiursoft.CommandFramework;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    public static NestedCommandApp Create() => new DmsNavigationApp()
        .WithFeature(new LoginCommand())
        .WithFeature(new Group("artifact", "Manage files uploaded to DMS or exported from a machine to DMS.",
        [
            ArtifactListCommand(),
            new ArtifactCommand("upload"),
            new ArtifactCommand("get"),
            new ArtifactCommand("download")
        ]))
        .WithFeature(new Group("access", "Check your permissions and manage Agent credentials or enrollment.",
        [
            new ManagementCommand("whoami", "identity"),
            new ManagementCommand("get", "access"),
            new ManagementCommand("issue-agent-key", "key"),
            new ManagementCommand("create-join-key", "join-create"),
            new ManagementCommand("list-join-keys", "join-list"),
            new ManagementCommand("revoke-join-key", "join-revoke"),
            new ManagementCommand("list-enrollments", "enrollments")
        ]))
        .WithFeature(new Group(MachineWorkflowCli.Machine, "List managed machines and request operations on them.",
        [
            StandardCommand("list",
                "Print the machine inventory. Requires Run open workflows.",
                ClusterWorkflows.GetMachines, _ => new EmptyInput()),
            StandardCommand("disk-usage",
                "Summarize filesystem capacity and usage across selected machines.",
                ClusterWorkflows.DiskUsage, _ => new EmptyInput()),
            AddMachineCommand(),
            new Group("docker", "Inspect and control standalone Docker containers.",
            [
                DockerPsCommand(),
                DockerLogsCommand(),
                DockerExecCommand(),
                DockerKillCommand(),
                NewDockerInspectCommand(),
                NewDockerStatsSampleCommand(),
                NewDockerEventsCommand(),
                NewDockerDiagnoseStartupCommand(),
                NewDockerStopCommand(),
                NewDockerStartCommand(),
                NewDockerRestartCommand(),
                new Group("filesystem", "Export container files or copy files between a container and its host.",
                [
                    NewDockerFilesystemExportCommand(),
                    NewDockerFilesystemCpToCommand(),
                    NewDockerFilesystemCpFromCommand()
                ]),
                new Group("system", "Inspect Docker storage usage.",
                [
                    StandardCommand("df",
                        "Show disk space used by Docker images, containers, volumes, and build cache.",
                        MachineWorkflows.DockerDiskUsage, _ => new EmptyInput())
                ])
            ]),
            new Group("filesystem", "Inspect, move, transfer, and delete files on a machine.",
            [
                DirectoryUsageCommand(),
                RemovePathCommand(),
                NewFilesystemLsCommand(),
                NewFilesystemStatCommand(),
                NewFilesystemCatCommand(),
                NewFilesystemCatCommand(asRoot: true),
                NewFilesystemTailCommand(),
                NewFilesystemTailCommand(asRoot: true),
                NewFilesystemDownloadCommand(),
                NewFilesystemDownloadCommand(asRoot: true),
                NewFilesystemFindCommand(),
                NewFilesystemMkdirCommand(),
                NewFilesystemCpCommand(),
                NewFilesystemMvCommand(),
                NewFilesystemExportCommand(),
                NewFilesystemImportCommand()
            ]),
            new Group("frps", "Inspect or deploy the FRP server on a machine.",
            [
                StandardCommand("status", "Report FRPS versions, systemd state, listeners, and recent journal counts.",
                    MachineWorkflows.FrpsStatus, _ => new EmptyInput()),
                FrpsDeployCommand()
            ]),
            new Group(MachineWorkflowCli.Box, "Inspect or deploy the Box repository on a machine.",
            [
                StandardCommand("inspect",
                    "Check the local Box repository's branch, commit, and working tree.",
                    MachineWorkflows.BoxInspect, _ => new EmptyInput()),
                BoxSetGitSshIdentityCommand(),
                BoxDeployCommand(),
                new BoxSecretCommand(BoxSecretCommandKind.List),
                new BoxSecretCommand(BoxSecretCommandKind.Summary),
                new BoxSecretCommand(BoxSecretCommandKind.Set),
                new BoxSecretCommand(BoxSecretCommandKind.Raw),
                new Group(MachineWorkflowCli.Puller, "Refresh images with the Box Puller.",
                [
                    PullerCommand()
                ])
            ]),
            new Group("performance", "Inspect processes and diagnose CPU, memory, and I/O usage.",
            [
                StandardCommand("info",
                    "Show a machine's CPU, memory, and load snapshot.",
                    MachineWorkflows.Performance, _ => new EmptyInput()),
                NewPerformanceProcessesCommand(),
                NewPerformanceCpuSampleCommand(),
                NewPerformanceMemoryBreakdownCommand(),
                NewPerformanceIoSampleCommand(),
                NewPerformanceDstatSampleCommand(),
                NewPerformancePressureCommand(),
                NewPerformanceOomHistoryCommand(),
                HistorySourcesCommand(),
                new Group("history", "Read historical performance samples from the selected machine.",
                [
                    HistoryCommand("cpu",
                        "Read recorded CPU utilization.",
                        OperationsWorkflows.PerformanceHistoryCpu, "--cpu"),
                    HistoryCommand("load",
                        "Read recorded system load averages.",
                        OperationsWorkflows.PerformanceHistoryLoad),
                    HistoryCommand("memory",
                        "Read recorded memory and swap usage.",
                        OperationsWorkflows.PerformanceHistoryMemory),
                    HistoryCommand("disk",
                        "Read recorded block-device I/O.",
                        OperationsWorkflows.PerformanceHistoryDisk, "--device"),
                    HistoryCommand("network",
                        "Read recorded network interface activity.",
                        OperationsWorkflows.PerformanceHistoryNetwork, "--interface")
                ]),
                PcpHistoryCommand(),
                BlockedProcessesCommand()
            ]),
            new Group("systemctl", "Inspect and control systemd services.",
            [
                ServiceCommand("restart", "Restart a systemd service.", MachineWorkflows.Restart),
                ServiceCommand("status",
                    "Read the current status of a systemd service.",
                    MachineWorkflows.ServiceStatus),
                NewSystemctlListCommand(),
                NewSystemctlStartCommand(),
                NewSystemctlStopCommand()
            ]),
            new Group(MachineWorkflowCli.Agent, "Check Agent health or install the Controller-provided release.",
            [
                StandardCommand(MachineWorkflowCli.AgentStatus.Name,
                    "Check Agent version, connection identity, and pending events.",
                    MachineWorkflowCli.AgentStatus.Definition, _ => new EmptyInput()),
                new ActionCommand("upgrade",
                    "Upgrade the Agent and confirm that the new version reconnects.",
                    WorkflowNames.AgentUpgrade, MachineWorkflows.AgentUpgrade)
            ]),
            new Group("layout", "Inspect disks, filesystems, mounts, and storage health.",
            [
                StandardCommand("lsblk",
                    "List disks, partitions, and their filesystems.",
                    MachineWorkflows.ListBlockDevices, _ => new EmptyInput()),
                StandardCommand("df",
                    "Show used and available space on mounted filesystems.",
                    MachineWorkflows.DiskFree, _ => new EmptyInput()),
                NewLayoutFdiskQueryCommand(),
                NewLayoutMountsCommand(),
                NewLayoutInodesCommand(),
                NewLayoutOpenDeletedCommand(),
                NewLayoutSmartCommand(),
                NewLayoutSmartTestCommand(),
                NewLayoutBcacheInfoCommand(),
                NewLayoutBcacheAuditCommand()
            ]),
            new Group("nvidia", "Inspect NVIDIA GPUs, drivers, and workload usage.",
            [
                NewNvidiaSmiCommand(),
                NewNvidiaOverviewCommand(),
                NewNvidiaProcessesCommand(),
                NewNvidiaSampleCommand(),
                NewNvidiaErrorsCommand(),
                NewNvidiaTopologyCommand(),
                NewNvidiaDriverInfoCommand()
            ]),
            new Group("swarm", "Inspect or operate Docker Swarm services.",
            [
                new Group("service", "Inspect service tasks and manage Swarm rollout, scaling, or rollback.",
                [
                    NewSwarmServiceListCommand(),
                    NewSwarmServiceInspectCommand(),
                    NewSwarmServicePsCommand(),
                    NewSwarmServiceLogsCommand(),
                    NewSwarmServiceRolloutStatusCommand(),
                    NewSwarmServiceStopCommand(),
                    NewSwarmServiceStartCommand(),
                    NewSwarmServiceUpdateCommand(),
                    NewSwarmServiceRollbackCommand()
                ])
            ]),
            new Group("system", "Check system details, read logs, or request a reboot or script run.",
            [
                NewSystemInfoCommand(),
                NewSystemUptimeCommand(),
                NewSystemJournalCommand(),
                NewSystemTimersCommand(),
                NewSystemRebootCommand(),
                NewSystemPoweroffCommand(),
                NewSystemExecuteScriptCommand(),
                KernelEventsCommand()
            ]),
            new Group("apt", "Inspect, refresh, upgrade, or clean up APT packages.",
            [
                NewAptInspectCommand(),
                NewAptInstallCommand(),
                NewAptUpgradeCommand(),
                NewAptUpdateCommand(),
                new ActionCommand("plan", "Show the package changes from an APT preview workflow.", "apt-plan", positionalOnly: true),
                NewAptMaintenanceCommand(AptMaintenance.UpgradeAll),
                NewAptMaintenanceCommand(AptMaintenance.Autoremove)
            ]),
            new Group("database", "Inspect and query configured database containers on a machine.",
            [
                NewDatabaseDiscoverCommand(),
                NewDatabaseListDatabasesCommand(),
                NewDatabaseListTablesCommand(),
                NewDatabaseDescribeTableCommand(),
                NewDatabaseQueryTableCommand(),
                NewDatabaseQuerySqlCommand(),
                NewDatabaseExecuteSqlCommand(),
                NewDatabaseDiagnosticsCommand()
            ]),
            ClickHouseCommands(),
            new Group("networking", "Inspect interfaces, routes, firewall rules, and connectivity.",
            [
                NewNetworkingInterfacesCommand(),
                NewNetworkingRoutesCommand(),
                NewNetworkingSocketsCommand(),
                NewNetworkingDnsQueryCommand(),
                NewNetworkingProbeCommand(),
                NewNetworkingTrafficSampleCommand(),
                NewNetworkingFirewallInfoCommand(),
                NewNetworkingTlsCheckCommand()
            ]),
            new Group(MachineWorkflowCli.Ssh, "Open time-limited SSH access or close it immediately.",
            [
                NewSshOpenCommand(),
                StandardCommand(MachineWorkflowCli.SshDisable.Name,
                    "Stop SSH, revoke the temporary lease, and verify closure. Existing ordinary SSH sessions may remain.",
                    MachineWorkflowCli.SshDisable.Definition, _ => new EmptyInput())
            ]),
            new Group("security", "Check host security controls and authentication events.",
            [
                NewSecuritySecureBootCommand(),
                NewSecurityDiskEncryptionCommand(),
                NewSecuritySshAuditCommand(),
                NewSecurityFail2banStatusCommand(),
                NewSecurityCleanTrafficStatusCommand(),
                NewSecurityAuthEventsCommand(),
                NewSecurityAccountsAuditCommand(),
                NewSecurityBaselineAuditCommand()
            ])
        ]))
        .WithFeature(new Group("lockbox", "View, approve, reject, or withdraw Lockbox requests.",
        [
            new ActionCommand("list", "List Lockbox requests, optionally filtered by decision.", "lockboxes"),
            new ActionCommand("status",
                "Show the requester, decision, eligible approvers, and equivalent command when available.",
                "lockbox-status", positionalOnly: true),
            new ActionCommand("get", "Read the full Lockbox record, including the submitted parameters.", "get"),
            new ActionCommand("approve", "Approve a pending Lockbox and queue its workflow.", "approve"),
            new ActionCommand("deny", "Reject a pending Lockbox request.", "deny"),
            new ActionCommand("withdraw", "Withdraw one of your pending Lockbox requests.", "withdraw")
        ]))
        .WithFeature(new Group("workflow", "Check execution status, results, child workflows, and trace.",
        [
            new ActionCommand("status",
                "Show a workflow's execution status and result.",
                "workflow", positionalOnly: true),
            new ActionCommand("callstack",
                "Show a workflow and its descendants as a tree.",
                "callstack", positionalOnly: true),
            new ActionCommand("result", "View a directory listing from a machine filesystem ls request.",
                "filesystem-result", positionalOnly: true),
            new ActionCommand("trace", "Read archived trace entries for a workflow and its descendants.", "trace", positionalOnly: true)
        ]))
        .WithFeature(new Group("operation", "Browse the workflow catalog, search audit history, or request cancellation.",
        [
            new ActionCommand("list-definitions",
                "List workflow definitions, risk levels, and your permissions.",
                "definitions"),
            new ActionCommand("query-audit", "Search archived Lockbox and workflow events.", "audit"),
            new ActionCommand("get-workflow", "Show a workflow's execution status and result.", "workflow"),
            new ActionCommand("get-callstack", "Show a workflow and its descendants as a tree.", "callstack"),
            new ActionCommand("get-trace", "Read archived trace entries for a workflow and its descendants.", "trace"),
            new ActionCommand("kill-workflow",
                "Request cancellation of a workflow and its descendants.",
                ClusterWorkflows.Cancel.Name, ClusterWorkflows.Cancel,
                inputFromWorkflowId: id => ClusterWorkflows.Cancel.Serialize(new CancelWorkflowInput(id)))
        ]));

    private static ActionCommand ArtifactListCommand()
    {
        var limit = new Option<int>("--limit")
            { DefaultValueFactory = _ => new ArtifactListInput().Limit, Description = "Number of artifacts to return, from 1 to 20." };
        var offset = new Option<int>("--offset")
            { Description = "Skip this many artifacts, ordered newest first." };
        return StandardCommand("ls", """
            List files you uploaded to DMS or exported from a machine. Requires Run open workflows.
            Download a file using its listed ID.
              dms artifact download --id <ArtifactId> --file ./report.tar
            """,
            ClusterWorkflows.ListArtifacts, context => new ArtifactListInput(context.GetValue(offset), context.GetValue(limit)), limit, offset);
    }

    private static ActionCommand AddMachineCommand()
    {
        var name = new Option<string>("--name")
            { Required = true, Description = "Name to give the new machine in the Controller inventory." };
        return StandardCommand("add", "Add a machine record to the Controller inventory.",
            ClusterWorkflows.AddMachine, p => new AddMachineInput(p.GetValue(name)!), name);
    }

    private static ActionCommand StandardCommand<TInput, TResult>(string name,
        string description,
        WorkflowDefinition<TInput, TResult> definition,
        Func<ParseResult, TInput> input,
        params Option[] options) =>
        new(name, description, definition.Name, definition, context => definition.Serialize(input(context)), options,
            readResult: definition.Risk == RiskLevel.Open ? result => definition.ReadResult(result)! : null);

    private static ActionCommand FrpsDeployCommand()
    {
        var token = new Option<string?>("--frps-token")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "Required for a fresh installation. Omit or leave empty to retain existing authentication."
        };
        return StandardCommand("deploy", "Install or upgrade the FRP server.",
            MachineWorkflows.FrpsDeploy, context => new FrpsDeployInput(context.GetValue(token)), token);
    }

    private static ActionCommand BoxDeployCommand()
    {
        var stage = new Option<int>("--stage")
        {
            DefaultValueFactory = _ => 4, Description = "Starting stage, 0 to 4 (default 4). All later stages also run."
        };
        var filter = new Option<string?>("--filter")
        {
            Description =
                "Literal stage-4 stack-name substring; omit for all stacks. " +
                "Earlier stages and shared preparation are not filtered."
        };
        return StandardCommand("deploy",
            "Update the local Box repository and deploy from the selected stage.",
            MachineWorkflows.BoxDeploy,
            context => new BoxDeployInput(context.GetValue(stage), context.GetValue(filter)),
            stage,
            filter);
    }

    private static ActionCommand DirectoryUsageCommand()
    {
        var path = new Option<string>("--path")
            { Required = true, Description = "Absolute directory path on the machine; contents are not read." };
        var depth = new Option<int>("--depth")
        {
            DefaultValueFactory = _ => 1,
            Description = "Directory depth, 0 to 3 (default 1). Scan is limited to one filesystem and 60 seconds."
        };
        return StandardCommand("du",
            "Measure disk usage under a directory.",
            MachineWorkflows.DirectoryUsage,
            context => new DirectoryUsageInput(context.GetValue(path)!, context.GetValue(depth)),
            path,
            depth);
    }

    private static ActionCommand RemovePathCommand()
    {
        var path = new Option<string>("--path")
        {
            Required = true,
            Description = "One exact absolute path to delete. No wildcard expansion; deletion is permanent."
        };
        var recursive = new Option<bool>("--recursive")
        {
            Description =
                "Also remove directory contents without crossing to a different " +
                "filesystem device. Symlink parents are rejected."
        };
        return StandardCommand("rm",
            "Permanently remove the specified file or directory.",
            MachineWorkflows.RemovePath,
            context => new RemovePathInput(context.GetValue(path)!, context.GetValue(recursive)),
            path,
            recursive);
    }

    private static ActionCommand BoxSetGitSshIdentityCommand()
    {
        var identity = new Option<string>("--identity-file")
            { Required = true, Description = "Existing machine-local, private, read-only Box deployment key file." };
        var hosts = new Option<string>("--known-hosts-file")
            { Required = true, Description = "Existing machine-local file containing verified GitLab host keys." };
        var command = StandardCommand("set-git-ssh-identity",
            "Select the existing machine-local SSH key and known-hosts file for Box Git pulls.",
            MachineWorkflows.BoxConfigureGit,
            context => new BoxGitInput(context.GetValue(identity)!, context.GetValue(hosts)!),
            identity,
            hosts);
        command.CommandAliases = ["configure-git"];
        return command;
    }

    private static ActionCommand PullerCommand()
    {
        var image = new Option<string?>(MachineWorkflowCli.ImageOption)
            { Description = "One image; omit to refresh all images configured in the selected Puller." };
        var puller = new Option<string>(MachineWorkflowCli.PullerOption)
            { Description = "Puller to refresh. Use main (default) or cn." };
        return StandardCommand(MachineWorkflowCli.PullerForceUpdate.Name,
            "Refresh one image or all configured images through the Box Puller.",
            MachineWorkflowCli.PullerForceUpdate.Definition,
            context => new PullerForceUpdateInput(context.GetValue(image), context.GetValue(puller) ?? "main"),
            image,
            puller);
    }

    private static ActionCommand DockerPsCommand()
    {
        var all = new Option<bool>("--all") { Description = "Include stopped containers." };
        var name = new Option<string?>("--grep-name")
            { Description = "Only containers whose names contain this literal text." };
        return StandardCommand("ps",
            "List Docker containers and their full IDs.",
            MachineWorkflows.DockerPs,
            context => new DockerPsInput(context.GetValue(all), context.GetValue(name)),
            all,
            name);
    }

    private static Option<string> ContainerOption() => new("--container")
    {
        Required = true, Description = "Full container ID from dms machine docker ps."
    };

    private static ActionCommand DockerLogsCommand()
    {
        var container = ContainerOption();
        var tail = new Option<int>("--tail")
            { DefaultValueFactory = _ => 100, Description = "Last 1 to 1000 lines (default 100)." };
        var since = new Option<DateTimeOffset?>("--since")
            { Description = "Only logs since this timestamp with UTC offset." };
        return StandardCommand("logs",
            "Read recent stdout and stderr from a Docker container.",
            MachineWorkflows.DockerLogs,
            context => new DockerLogsInput(context.GetValue(container)!,
                context.GetValue(tail),
                context.GetValue(since)),
            container,
            tail,
            since);
    }

    private static ActionCommand DockerExecCommand()
    {
        var container = ContainerOption();
        var command = new Argument<string[]>("command")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "Executable and arguments after --. Include the shell explicitly when needed."
        };
        return new ActionCommand("exec",
            "Run an executable inside a Docker container.",
            MachineWorkflows.DockerExec.Name,
            MachineWorkflows.DockerExec,
            context => MachineWorkflows.DockerExec.Serialize(new DockerExecInput(context.GetValue(container)!,
                context.GetValue(command)!)),
            [
                container
            ],
            command);
    }

    private static ActionCommand DockerKillCommand()
    {
        var container = ContainerOption();
        return StandardCommand("kill",
            "Send SIGKILL to a running Docker container.",
            MachineWorkflows.DockerKill,
            context => new DockerContainerInput(context.GetValue(container)!),
            container);
    }

    private static ActionCommand ServiceCommand(string name,
        string description,
        MachineWorkflowDefinition<ServiceInput, ProcessResult> definition)
    {
        var service = new Option<string>("--service")
            { Required = true, Description = "systemd unit name, such as nginx.service." };
        return StandardCommand(name,
            description,
            definition,
            context => new ServiceInput(context.GetValue(service) ?? string.Empty),
            service);
    }

    private sealed class DmsNavigationApp : NestedCommandApp
    {
        public DmsNavigationApp()
        {
            RootCommand.Description = "Request server operations, check approvals, and inspect workflow results.";
            RootCommand.SetAction(new HelpAction().Invoke);
        }
    }

    private sealed class Group(string name, string description, CommandHandlerBuilder[] children)
        : NavigationCommandHandlerBuilder
    {
        protected override string Name => name;
        protected override string Description => description;
        protected override CommandHandlerBuilder[] GetSubCommandHandlers() => children;

        public override Command BuildAsCommand()
        {
            var command = base.BuildAsCommand();
            command.SetAction(new HelpAction().Invoke);
            return command;
        }
    }

    private sealed class ActionCommand(
        string name,
        string description,
        string action,
        WorkflowDefinition? definition = null,
        Func<ParseResult, string>? input = null,
        Option[]? options = null,
        Argument? argument = null,
        Func<DmsApiClient, ParseResult, Guid, Task<string>>? preparedInput = null,
        bool positionalOnly = false,
        Func<string, object>? readResult = null,
        Func<Guid, string>? inputFromWorkflowId = null,
        Func<ParseResult, (string Workflow, string Input)>? selectOperation = null,
        string? targetDescription = null) : DmsCommandHandler(supportsLog: action == "trace")
    {
        public string[] CommandAliases { private get; set; } = [];
        protected override string[] Alias => CommandAliases;

        public override Command BuildAsCommand()
        {
            var command = base.BuildAsCommand();
            if (argument != null) command.Arguments.Add(argument);
            if (UsesId)
            {
                _positionalId.Arity = positionalOnly ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne;
                _positionalId.Description = UsesLockboxId ? "Lockbox ID (lb/<UUID>)." : "Workflow ID.";
                command.Arguments.Add(_positionalId);
                command.Validators.Add(ValidateId);
            }
            return command;
        }

        private bool UsesLockboxId =>
            action is "lockbox-status" or "get" or "approve" or "deny" or "withdraw" or "clickhouse-result";
        private bool UsesId => UsesLockboxId ||
            action is "workflow" or "callstack" or "trace" or "apt-plan" or "filesystem-result" ||
            inputFromWorkflowId != null;
        private bool IsSubmission => definition != null;
        protected override string Name => name;

        protected override string Description => description;

        private readonly Argument<string?> _positionalId = new("id");

        private readonly Option<Guid[]> _machines = new(MachineWorkflowCli.MachineIdOption)
        {
            AllowMultipleArgumentsPerToken = true,
            Description = "Target machine IDs. Omit when exactly one machine is enabled."
        };

        private readonly Option<bool> _all = new("--all-machines")
            { Description = "Select every enabled machine at submission time." };
        private readonly Option<Guid> _id = new("--id")
            { Description = "Workflow ID from a Lockbox or workflow query." };

        private readonly Option<Guid> _request = new("--request-id")
            { Description = "Reuse this ID only to retry the same submission." };

        private readonly Option<bool> _showLockboxId = new("--show-lockbox-id")
            { Description = "Print the Lockbox and workflow IDs immediately after submission." };

        private readonly Option<string> _reason = new(MachineWorkflowCli.ReasonOption);
        private readonly Option<int> _skip = new("--skip")
            { Description = "Skip this many trace entries before returning the next page." };
        private readonly Option<bool> _recursive = new("--recursive")
        {
            DefaultValueFactory = _ => true,
            Description = "Include descendants in the time-ordered trace page (default: true). Use --recursive false for only the specified workflow."
        };

        private readonly Option<DateTimeOffset?> _from = new("--from")
        {
            Description =
                "Inclusive archive recording time, ISO 8601 with UTC offset; defaults to 24 hours before --until."
        };

        private readonly Option<DateTimeOffset?> _until = new("--until")
        {
            Description =
                "Exclusive archive recording time, ISO 8601 with UTC offset; defaults to now. Maximum range: 31 days."
        };

        private readonly Option<Guid?> _machineFilter = new("--machine-id")
            { Description = "Filter events to the specified machine ID." };
        private readonly Option<Guid?> _workflowFilter = new("--workflow-id")
            { Description = "Filter events to the specified workflow ID." };
        private readonly Option<Guid?> _rootFilter = new("--root-workflow-id")
            { Description = "Filter events by root workflow ID to include an execution tree." };
        private readonly Option<LockboxId?> _lockboxFilter = new("--lockbox-id")
            { Description = "Filter events to the specified Lockbox ID (lb/<UUID>).", CustomParser = ParseLockboxId };

        private readonly Option<LockboxId> _lockboxId = new("--id")
            { Description = "Lockbox ID, lb/<UUID>.", CustomParser = result => ParseLockboxId(result) ?? default };

        private readonly Option<DecisionState?> _decision = new("--decision")
            { Description = "Filter by Pending, Approved, Denied, or Withdrawn." };
        private readonly Option<string?> _requester = new("--requester-id")
            { Description = "Filter events to the specified requester user ID." };
        private readonly Option<string?> _approver = new("--approver-id")
            { Description = "Filter events to the specified approver user ID." };
        private readonly Option<string?> _definitionFilter = new("--definition")
            { Description = "Filter by the workflow definition recorded for the event." };
        private readonly Option<string?> _requestedDefinition = new("--requested-definition")
            { Description = "Filter by the workflow the Lockbox originally requested." };
        private readonly Option<WorkflowAuditKind?> _kind = new("--kind")
            { Description = "Filter by audit event type." };
        private readonly Option<RunState?> _state = new("--state")
            { Description = "Filter by the workflow state recorded in the event." };
        private readonly Option<WorkflowOrigin?> _origin = new("--origin") { Description = "Filter by Manual or Automatic execution origin." };
        private readonly Option<bool?> _automatic = new("--automatic-approval")
            { Description = "Filter decisions by whether approval was automatic." };

        private readonly Option<int> _page = new("--page")
        {
            Description = action == "lockboxes" ? "Zero-based page of 50 Lockbox requests." : "Zero-based page of 50 audit events."
        };

        private static LockboxId? ParseLockboxId(ArgumentResult result)
        {
            if (result.Tokens.Count == 0) return null;
            if (LockboxId.TryParse(result.Tokens[0].Value, out var id)) return id;
            result.AddError("Expected a Lockbox ID in the form lb/<UUID>.");
            return null;
        }

        private void ValidateId(CommandResult result)
        {
            var positional = result.GetValue(_positionalId);
            var option = UsesLockboxId ? (Option)_lockboxId : _id;
            var suppliedOption = result.GetResult(option) != null;
            if (!positionalOnly && positional == null && !suppliedOption)
                result.AddError(UsesLockboxId ? "Provide a Lockbox ID (lb/<UUID>)." : "Provide a Workflow ID.");
            else if (positional != null && suppliedOption)
                result.AddError("Pass the ID once, as an argument or with --id.");
            else if (positional != null && !(UsesLockboxId ? LockboxId.TryParse(positional, out _) : Guid.TryParse(positional, out _)))
                result.AddError(UsesLockboxId ? "Expected a Lockbox ID in the form lb/<UUID>." : "Expected a Workflow UUID.");
        }

        protected override IEnumerable<Option> GetActionOptions()
        {
            if (IsSubmission)
            {
                _reason.Required = readResult == null;
                _reason.Description = readResult == null
                    ? "Reason recorded in the Lockbox. Execution starts after approval."
                    : "Optional reason for the automatically approved Lockbox.";
                yield return _request;
                yield return _reason;
                if (readResult != null) yield return _showLockboxId;
                if (definition?.RequiresTargets == true)
                {
                    if (definition.SingleMachineOnly)
                    {
                        _machines.AllowMultipleArgumentsPerToken = false;
                        _machines.Description = "Target machine ID. Omit when exactly one machine is enabled.";
                    }
                    if (targetDescription != null) _machines.Description = targetDescription;
                    yield return _machines;
                    if (!definition.SingleMachineOnly) yield return _all;
                }

                foreach (var option in options ??
                         [
                         ]) yield return option;
                if (inputFromWorkflowId != null) yield return _id;
            }
            else if (action == "audit")
            {
                foreach (var option in new Option[]
                         {
                             _from, _until, _machineFilter, _workflowFilter, _rootFilter, _lockboxFilter,
                             _requester, _approver, _definitionFilter, _requestedDefinition, _kind, _state, _automatic, _origin,
                             _page
                         }) yield return option;
            }
            else if (action == "lockboxes")
            {
                yield return _page;
                yield return _decision;
            }
            else if (UsesLockboxId)
            {
                if (!positionalOnly) yield return _lockboxId;
                if (action is "approve" or "deny")
                {
                    _reason.Description = "Optional explanation recorded with the approval or rejection.";
                    yield return _reason;
                }
            }
            else if (UsesId)
            {
                if (!positionalOnly) yield return _id;
                if (action == "trace")
                {
                    yield return _skip;
                    yield return _recursive;
                }
            }
        }

        protected override bool Callstack => action == "callstack";
        protected override bool Catalog => action == "definitions";
        protected override bool Audit => action == "audit";
        protected override bool LockboxStatus => action == "lockbox-status";

        protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
        {
            var positional = UsesId ? context.GetValue(_positionalId) : null;
            var id = !UsesLockboxId && positional != null ? Guid.Parse(positional) : context.GetValue(_id);
            var lockboxId = UsesLockboxId && positional != null ? LockboxId.Parse(positional) : context.GetValue(_lockboxId);
            DmsResponse response;
            if (IsSubmission)
            {
                var requestId = context.GetValue(_request);
                if (requestId == Guid.Empty) requestId = Guid.NewGuid();
                var selected = selectOperation?.Invoke(context);
                response = await client.SubmitAsync(new OperationRequest
                {
                    RequestId = requestId,
                    Workflow = selected?.Workflow ?? action, MachineIds = (context.GetValue(_machines) ??
                    [
                    ]).ToList(),
                    AllMachines = context.GetValue(_all), Reason = context.GetValue(_reason) ?? string.Empty,
                    Input = selected?.Input ?? (action == WorkflowNames.AgentUpgrade
                        ? MachineWorkflows.AgentUpgrade.Serialize(
                            new AgentUpgradeInput((await client.AgentReleasesAsync()).Data))
                        : preparedInput != null
                            ? await preparedInput(client, context, requestId)
                            : inputFromWorkflowId?.Invoke(id) ?? input?.Invoke(context) ?? "{}")
                });
                if (ShouldWaitForOpenResult(context))
                    response = await ReadOpenResultAsync(client, response, requestId, context);
            }
            else if (action == "clickhouse-result")
            {
                var (reply, result) = await ReadClickHouseResultAsync(client, lockboxId, context.InvocationConfiguration.Error);
                reply.Data = ClickHouseOutput(result);
                response = reply;
            }
            else
                response = action switch
                {
                    "audit" => await client.AuditEventsAsync(new WorkflowAuditQuery
                    {
                        From = context.GetValue(_from), Until = context.GetValue(_until),
                        MachineId = context.GetValue(_machineFilter), WorkflowId = context.GetValue(_workflowFilter),
                        RootWorkflowId = context.GetValue(_rootFilter), LockboxId = context.GetValue(_lockboxFilter),
                        RequesterId = context.GetValue(_requester), ApproverId = context.GetValue(_approver),
                        Definition = context.GetValue(_definitionFilter),
                        RequestedDefinition = context.GetValue(_requestedDefinition),
                        Kind = context.GetValue(_kind), State = context.GetValue(_state),
                        Origin = context.GetValue(_origin), AutomaticApproval = context.GetValue(_automatic), Page = context.GetValue(_page)
                    }),
                    "definitions" => await client.ListDefinitionsAsync(),
                    "lockboxes" => await client.ListLockboxesAsync(context.GetValue(_page),
                        context.GetValue(_decision)),
                    "lockbox-status" => await client.LockboxStatusAsync(lockboxId, includeDefinition: SelectedFormat(context) == OutputFormat.Table),
                    "get" => await client.GetLockboxAsync(lockboxId),
                    "withdraw" => await client.WithdrawAsync(lockboxId),
                    "approve" or "deny" => await client.DecideAsync(lockboxId,
                        action == "approve",
                        context.GetValue(_reason) ?? string.Empty),
                    "workflow" => await client.GetWorkflowAsync(id),
                    "callstack" => await client.CallstackAsync(id),
                    "apt-plan" => await ReadAptPlanAsync(client, id),
                    "filesystem-result" => await client.FilesystemResultAsync(id),
                    "trace" => await client.TraceAsync(id, context.GetValue(_skip), context.GetValue(_recursive)),
                    _ => throw new InvalidOperationException("Unsupported command.")
                };

            return response;
        }

        private bool ShouldWaitForOpenResult(ParseResult context) =>
            readResult != null && !context.GetValue(_showLockboxId);

        protected override void WriteResponse(ParseResult context, DmsResponse response, OutputFormat format, bool color, int width)
        {
            if (ShouldWaitForOpenResult(context) || action is "apt-plan" or "clickhouse-result")
                DmsOutputWriter.WriteResult(context.InvocationConfiguration.Output, response.Data!, format, color, width);
            else if (action == "filesystem-result")
                DmsOutputWriter.WriteFilesystemResult(context.InvocationConfiguration.Output, context.InvocationConfiguration.Error,
                    ((JObject)response.Data!).ToObject<FilesystemListResult>()!, format, color, width);
            else
            {
                base.WriteResponse(context, response, format, color, width);
                if (format == OutputFormat.Table && response.Data is JObject data)
                    WriteDirectoryNextStep(context.InvocationConfiguration.Output, data);
            }
        }

        private void WriteDirectoryNextStep(TextWriter output, JObject data)
        {
            Guid? workflowId;
            if (action == OperationsWorkflows.FilesystemLs.Name && data.ToObject<LockboxReceipt>() is { } receipt)
            {
                workflowId = receipt.WorkflowId;
                if (workflowId == null)
                {
                    output.WriteLine();
                    output.WriteLine("Check approval status with this command.");
                    output.WriteLine($"  dms lockbox status {receipt.LockboxId}");
                    return;
                }
            }
            else if (action is "lockbox-status" or "approve" &&
                     data["Definition"]?.Value<string>() == OperationsWorkflows.FilesystemLs.Name &&
                     data["Decision"]?.Value<int>() == (int)DecisionState.Approved &&
                     Guid.TryParse(data["WorkflowId"]?.ToString(), out var approvedWorkflowId))
                workflowId = approvedWorkflowId;
            else
                return;

            output.WriteLine();
            output.WriteLine("View the directory listing after execution. Requires Read DMS operations.");
            output.WriteLine($"  dms workflow result {workflowId}");
        }

        private async Task<DmsResponse> ReadOpenResultAsync(DmsApiClient client, DmsResponse response, Guid requestId,
            ParseResult context)
        {
            var receipt = (response.Data as JObject)?.ToObject<LockboxReceipt>()
                ?? throw new ArgumentException("The Controller returned no Lockbox receipt.");
            var workflowId = receipt.WorkflowId ?? throw new ArgumentException(
                $"Lockbox {receipt.LockboxId} has no approved execution. Check its status before submitting a new request.");
            try
            {
                if (ClickHouseWorkflows.IsClickHouse(action))
                {
                    var (clickHouse, result) = await ReadClickHouseResultAsync(client, receipt.LockboxId, context.InvocationConfiguration.Error);
                    clickHouse.Data = ClickHouseOutput(result);
                    return clickHouse;
                }
                var reply = await client.WaitForOpenResultAsync(receipt.LockboxId);
                var run = (reply.Data as JObject)?.ToObject<OpenWorkflowResult>();
                if (run == null || run.WorkflowId != workflowId)
                    throw new ArgumentException("The Controller returned an unexpected workflow result.");
                if (run.State is RunState.Succeeded or RunState.Warning)
                {
                    var payload = run.Result?.Trim();
                    var result = payload is null or "" or "null" ? null : readResult!(payload);
                    if (run.State == RunState.Warning)
                        context.InvocationConfiguration.Error.WriteLine($"Warning: Workflow {workflowId} reported warnings.");
                    reply.Data = result is null or EmptyResult
                        ? new { run.WorkflowId, State = run.State.ToString() }
                        : result;
                    return reply;
                }

                if (run.State.Terminal() || run.State == RunState.NeedsVerification)
                    throw new ArgumentException($"Workflow state is {run.State}.");
                throw new ArgumentException($"Timed out waiting for the result. Workflow state: {run.State}.");
            }
            catch
            {
                context.InvocationConfiguration.Error.WriteLine(
                    $"Lockbox {receipt.LockboxId}, Workflow {workflowId}. Retry the same request with --request-id {requestId}.");
                throw;
            }
        }
    }
}
