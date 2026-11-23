using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Client;

internal static class MachineWorkflowCli
{
    internal const string Machine = "machine";
    internal const string Agent = "agent";
    internal const string Box = "box";
    internal const string Puller = "puller";
    internal const string Ssh = "ssh";
    internal const string MachineIdOption = "--machine-id";
    internal const string ReasonOption = "--reason";
    internal const string PullerOption = "--puller";
    internal const string ImageOption = "--image";
    internal const string KeyIdOption = "--key-id";
    internal const string MinutesOption = "--minutes";
    internal const string SudoOption = "--sudo";

    // Pin the snapshot version each command can represent. A workflow contract
    // update needs an explicit review of its mapping before supporting a new version.
    internal static readonly MachineWorkflowCli<EmptyInput, AgentStatusResult> AgentStatus =
        new(MachineWorkflows.AgentStatus, "2", Agent, "status");

    internal static readonly MachineWorkflowCli<PullerForceUpdateInput, PullerForceUpdateResult> PullerForceUpdate =
        new(MachineWorkflows.PullerForceUpdate, "2", Box, "force-update", Puller);

    internal static readonly MachineWorkflowCli<SshOpenInput, OperationResult> SshOpen =
        new(OperationsWorkflows.SshOpen, "1", Ssh, "temp-allow-ssh");

    internal static readonly MachineWorkflowCli<EmptyInput, OperationResult> SshDisable =
        new(OperationsWorkflows.SshDisable, "1", Ssh, "disable");
}

internal sealed record MachineWorkflowCli<TInput, TResult>(
    MachineWorkflowDefinition<TInput, TResult> Definition, string SnapshotVersion, string Group, string Name,
    string? Subgroup = null)
{
    internal string Invocation => Subgroup is null
        ? $"dms {MachineWorkflowCli.Machine} {Group} {Name}"
        : $"dms {MachineWorkflowCli.Machine} {Group} {Subgroup} {Name}";

    internal bool Matches(string definition, string version) =>
        Definition.Name == definition && SnapshotVersion == version && Definition.Version == SnapshotVersion;
}
