using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public sealed class LockboxViewModel : UiStackLayoutViewModel
{
    public required LockboxRequest Item { get; init; }
    public required LockboxStatus Status { get; init; }
    public required LockboxSnapshot Snapshot { get; init; }
    public required IReadOnlyDictionary<Guid, string> MachineNames { get; init; }
    public required IReadOnlyList<ArtifactReference> InputArtifacts { get; init; }
    public string? Notice { get; init; }
    public bool CanDecide { get; init; }
    public bool CanWithdraw { get; init; }
    public bool OwnRequestView { get; init; }
    public bool CanViewOperations { get; init; }
    public RunState? ExecutionState { get; init; }
    public AgentUpgradeReview? AgentUpgrade { get; init; }
    public IReadOnlyDictionary<Guid, AptPlanResult>? AptPlans { get; init; }
    public bool CanResubmit { get; init; }
}

public sealed record AgentUpgradeReview(IReadOnlyList<AgentRelease> Requested, IReadOnlyList<AgentRelease>? Available)
{
    public bool RequiresResubmission => Available is not null && !AgentReleaseStore.Matches(Requested, Available);
}

public sealed record LockboxSnapshot(
    IReadOnlyList<Guid> Targets, bool AllMachines, JsonElement Input, string RequestJson, LockboxCommand? Command)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static Guid[] ReadTargets(LockboxRequest item) => JsonSerializer.Deserialize<Guid[]>(item.TargetMachines) ?? [];

    public static LockboxSnapshot Read(LockboxRequest item)
    {
        var targets = ReadTargets(item);
        using var stored = JsonDocument.Parse(item.Parameters);
        var request = stored.RootElement.Deserialize<OperationRequest>()!;
        LegacyClusterWorkflowInput.Normalize(request, item.Definition);
        using var input = JsonDocument.Parse(request.Input);

        return new LockboxSnapshot(targets, request.AllMachines, input.RootElement.Clone(),
            Format(stored.RootElement), ReadCommand(item, request.Input, targets));
    }

    internal static string Format(JsonElement value) => JsonSerializer.Serialize(value, Indented);

    private static LockboxCommand? ReadCommand(LockboxRequest item, string input, IReadOnlyList<Guid> targets)
    {
        // Generate a preview only for workflow versions whose command projection is known.
        if (item.Definition == MachineWorkflows.DockerExec.Name && item.Version == "1")
        {
            var command = JsonSerializer.Deserialize<DockerExecInput>(input)!;
            return new LockboxCommand("docker exec " + Quote(command.Container),
                string.Join(' ', command.Command.Select(Quote)), IsEquivalentCli: false);
        }
        if (targets.Count == 0) return null;

        var options = new List<string> { "--machine-id " + string.Join(' ', targets) };
        string invocation;
        if (item.Definition == WorkflowNames.PullerForceUpdate && item.Version == "2")
        {
            var puller = JsonSerializer.Deserialize<PullerForceUpdateInput>(input)!;
            invocation = "dms machine box puller force-update";
            options.Add("--puller " + Quote(puller.Puller));
            if (puller.Image != null) options.Add("--image " + Quote(puller.Image));
        }
        else if (item.Definition == OperationsWorkflows.SshOpen.Name && item.Version == "1")
        {
            var lease = JsonSerializer.Deserialize<SshOpenInput>(input)!;
            invocation = "dms machine ssh temp-allow-ssh";
            options.Add($"--key-id {lease.KeyId}");
            options.Add($"--minutes {lease.Minutes}");
            if (lease.Sudo) options.Add("--sudo");
        }
        else if (item.Definition == OperationsWorkflows.SshDisable.Name && item.Version == "1")
        {
            invocation = "dms machine ssh disable";
        }
        else
        {
            return null;
        }
        options.Add("--reason " + Quote(item.Reason));
        return new LockboxCommand(invocation, string.Join(LockboxCommand.Continuation, options), IsEquivalentCli: true);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public sealed record LockboxCommand(string Invocation, string Arguments, bool IsEquivalentCli)
{
    internal const string Continuation = " \\\n  ";
    public string Separator => IsEquivalentCli ? Continuation : " ";
}
