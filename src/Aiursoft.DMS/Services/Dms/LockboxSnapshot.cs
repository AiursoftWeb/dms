using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using static Aiursoft.DMS.Client.MachineWorkflowCli;

namespace Aiursoft.DMS.Services.Dms;

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

    internal static string? ReadStatusCommand(LockboxRequest item)
    {
        try
        {
            var request = JsonSerializer.Deserialize<OperationRequest>(item.Parameters);
            if (request is null) return null;

            var command = ReadEquivalentCommand(item, request.Input, ReadTargets(item))?.CommandLine;
            // Never emit terminal control characters in command previews.
            return command?.Any(char.IsControl) == true ? null : command;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
        {
            return null;
        }
    }

    private static LockboxCommand? ReadCommand(LockboxRequest item, string input, IReadOnlyList<Guid> targets)
    {
        // Generate a preview only for workflow versions whose command projection is known.
        if (item.Definition == MachineWorkflows.DockerExec.Name && item.Version == "1")
        {
            var command = JsonSerializer.Deserialize<DockerExecInput>(input)!;
            return new LockboxCommand("docker exec " + Quote(command.Container),
                command.Command.Select(Quote).ToArray(), IsEquivalentCli: false);
        }

        try
        {
            return ReadEquivalentCommand(item, input, targets);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidDataException)
        {
            // Historical inputs that no longer match a CLI contract still have a parameter preview.
            return null;
        }
    }

    private static LockboxCommand? ReadEquivalentCommand(LockboxRequest item, string input, IReadOnlyList<Guid> targets)
    {
        if (targets.Count == 0 || item.Reason.Contains('\0')) return null;

        var options = new List<string> { MachineIdOption + " " + string.Join(' ', targets) };
        string invocation;
        if (AgentStatus.Matches(item.Definition, item.Version))
        {
            AgentStatus.Definition.Validate(input);
            invocation = AgentStatus.Invocation;
        }
        else if (PullerForceUpdate.Matches(item.Definition, item.Version))
        {
            var puller = PullerForceUpdate.Definition.ReadInput(input);
            invocation = PullerForceUpdate.Invocation;
            options.Add(PullerOption + " " + Quote(puller.Puller));
            if (puller.Image != null) options.Add(ImageOption + " " + Quote(puller.Image));
        }
        else if (SshOpen.Matches(item.Definition, item.Version) && targets.Count == 1)
        {
            var lease = SshOpen.Definition.ReadInput(input);
            invocation = SshOpen.Invocation;
            if (lease.KeyId != Guid.Empty) options.Add($"{KeyIdOption} {lease.KeyId}");
            options.Add(FormattableString.Invariant($"{MinutesOption} {lease.Minutes}"));
            if (lease.Sudo) options.Add(SudoOption);
        }
        else if (SshDisable.Matches(item.Definition, item.Version) && targets.Count == 1)
        {
            SshDisable.Definition.Validate(input);
            invocation = SshDisable.Invocation;
        }
        else
        {
            return null;
        }
        // Keep values that resemble an option or a response file in the option token.
        var reasonSeparator = item.Reason.StartsWith('-') || item.Reason.StartsWith('@') ? "=" : " ";
        options.Add(ReasonOption + reasonSeparator + Quote(item.Reason));
        return new LockboxCommand(invocation, options, IsEquivalentCli: true);
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}

public sealed record LockboxCommand(string Invocation, IReadOnlyList<string> Options, bool IsEquivalentCli)
{
    public string Separator => IsEquivalentCli ? " \\\n  " : " ";
    public string Arguments => string.Join(Separator, Options);
    internal string CommandLine => Invocation + " " + string.Join(' ', Options);
}
