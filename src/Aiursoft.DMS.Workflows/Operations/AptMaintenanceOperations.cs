using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed class AptPlanWorkflow(AptPlanInput input, AgentEnvironment agent)
    : MachineWorkflow<AptPlanInput, AptPlanResult>(OperationsWorkflows.AptPlan, input)
{
    public override async Task<AptPlanResult> ExecuteAsync(MachineWorkflowContext context, AptPlanInput value, CancellationToken token)
    {
        var architecture = await AptMaintenanceOperations.ArchitectureAsync(context, agent, token);
        var changes = await AptMaintenanceOperations.SimulateAsync(context, agent, value, architecture, token);
        AptPackageChange[] additional = [];
        if (value.Operation == AptMaintenance.UpgradeAll)
        {
            var full = await AptMaintenanceOperations.SimulateAsync(context, agent, value, architecture, token, fullUpgrade: true);
            if (full.Any(change => change.Action is AptPackageAction.Remove or AptPackageAction.Purge))
                additional = full.Where(change => change.Action == AptPackageAction.Upgrade && !changes.Contains(change)).ToArray();
        }

        var result = new AptPlanResult(changes, additional);
        if (JsonSerializer.Serialize(result).Length > 32000 ||
            OperationsWorkflows.AptUpgradeAll.Serialize(new AptApplyInput(context.Id, value.Purge, changes)).Length > 24000)
            throw new IOException("This machine's APT plan exceeds the workflow size limit. A new approval cannot use this preview.");

        if (changes.Length == 0) await context.TraceAsync("No package changes are planned.", "plan");
        await OperationOutput.TraceTextAsync(context, AptTransactionGuard.Describe(changes), "plan");
        if (additional.Length != 0)
            await OperationOutput.TraceTextAsync(context,
                "Additional upgrades in an APT plan that would remove packages. These upgrades are excluded from this request.\n" +
                AptTransactionGuard.Describe(additional), "plan");
        return result;
    }
}

internal static partial class AptMaintenanceOperations
{
    private static readonly IReadOnlyDictionary<string, string> AptLocale = new Dictionary<string, string> { ["LC_ALL"] = "C" };

    public static async Task<OperationResult> UpdateAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent,
        CancellationToken token)
    {
        // A failed source makes the refresh fail even if APT can keep an older copy of its index.
        await OperationOutput.CommandAsync(context, agent, "apt-get", ["update", "--error-on=any"], token, seconds: 600, variables: AptLocale);
        return new OperationResult("APT package indexes refreshed.");
    }

    public static Task<OperationResult> UpgradeAsync(MachineWorkflowContext context, AptApplyInput input, AgentEnvironment agent,
        CancellationToken token) =>
        ApplyAsync(context, input, new AptPlanInput(AptMaintenance.UpgradeAll, input.Purge), agent, token);

    public static Task<OperationResult> AutoremoveAsync(MachineWorkflowContext context, AptApplyInput input, AgentEnvironment agent,
        CancellationToken token) =>
        ApplyAsync(context, input, new AptPlanInput(AptMaintenance.Autoremove, input.Purge), agent, token);

    internal static async Task<string> ArchitectureAsync(MachineWorkflowContext context, AgentEnvironment agent, CancellationToken token) =>
        (await OperationOutput.CommandAsync(context, agent, "dpkg", ["--print-architecture"], token, variables: AptLocale)).Output.Trim();

    private static string[] Arguments(AptPlanInput plan)
    {
        plan.Validate();
        return plan.Operation == AptMaintenance.UpgradeAll
            ? ["--with-new-pkgs", "--no-remove", "-o", "APT::Get::AutomaticRemove=false", "upgrade"]
            : ["-o", $"APT::Get::Purge={plan.Purge.ToString().ToLowerInvariant()}", "autoremove"];
    }

    internal static async Task<AptPackageChange[]> SimulateAsync(MachineWorkflowContext context, AgentEnvironment agent,
        AptPlanInput plan, string architecture, CancellationToken token, bool fullUpgrade = false)
    {
        string[] arguments = fullUpgrade ? ["-o", "APT::Get::AutomaticRemove=false", "dist-upgrade"] : Arguments(plan);
        var result = await OperationOutput.CommandAsync(context, agent, "apt-get", ["--simulate", .. arguments],
            token, variables: AptLocale);
        return ParseSimulation(result.Output, architecture, plan, fullUpgrade);
    }

    internal static AptPackageChange[] ParseSimulation(string output, string architecture, AptPlanInput plan, bool fullUpgrade)
    {
        var changes = new List<AptPackageChange>();
        var configure = new List<(string Package, string Version)>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith("Inst ", StringComparison.Ordinal) && !line.StartsWith("Conf ", StringComparison.Ordinal) &&
                !line.StartsWith("Remv ", StringComparison.Ordinal) && !line.StartsWith("Purg ", StringComparison.Ordinal))
                continue;
            var action = SimulationLine().Match(line);
            if (!action.Success) throw new IOException("APT returned an unrecognized package action.");
            var package = AptTransactionGuard.PackageName(action.Groups["package"].Value, architecture);
            var before = action.Groups["before"].Success ? action.Groups["before"].Value : null;
            var after = action.Groups["after"].Success ? action.Groups["after"].Value : null;
            switch (action.Groups["action"].Value)
            {
                case "Inst" when after != null:
                    changes.Add(new AptPackageChange(before == null ? AptPackageAction.Install : AptPackageAction.Upgrade, package, before, after));
                    break;
                case "Conf" when after != null:
                    configure.Add((package, after));
                    break;
                case "Remv":
                    changes.Add(new AptPackageChange(AptPackageAction.Remove, package, before, null));
                    break;
                case "Purg":
                    changes.Add(new AptPackageChange(AptPackageAction.Purge, package, before, null));
                    break;
                default:
                    throw new IOException("APT returned an incomplete package action.");
            }
        }

        var summary = SimulationSummary().Match(output);
        if (!summary.Success ||
            changes.Count(c => c.Action == AptPackageAction.Upgrade) != Count(summary, "upgrading") ||
            changes.Count(c => c.Action == AptPackageAction.Install) != Count(summary, "installing") ||
            changes.Count(c => c.Action is AptPackageAction.Remove or AptPackageAction.Purge) != Count(summary, "removing") ||
            changes.Select(c => c.Package).Distinct(StringComparer.Ordinal).Count() != changes.Count ||
            configure.Any(c => !changes.Any(change => change.Package == c.Package && change.ToVersion == c.Version)))
            throw new IOException("APT's action list does not account for its full transaction. Check the package manager state before requesting maintenance.");

        if (!fullUpgrade && (plan.Operation == AptMaintenance.UpgradeAll
                ? changes.Any(change => change.Action is AptPackageAction.Remove or AptPackageAction.Purge)
                : changes.Any(change => change.Action != (plan.Purge ? AptPackageAction.Purge : AptPackageAction.Remove))))
            throw new IOException("APT proposed actions outside this maintenance operation.");

        return changes.OrderBy(c => c.Package, StringComparer.Ordinal).ToArray();
    }

    private static int Count(Match summary, string group) => int.Parse(summary.Groups[group].Value, CultureInfo.InvariantCulture);

    private static async Task<OperationResult> ApplyAsync(MachineWorkflowContext context, AptApplyInput input, AptPlanInput plan,
        AgentEnvironment agent, CancellationToken token)
    {
        var expected = input.Changes ?? throw new InvalidDataException("This APT workflow requires a Controller-approved per-machine plan.");
        var architecture = await ArchitectureAsync(context, agent, token);
        var current = await SimulateAsync(context, agent, plan, architecture, token);
        var difference = AptTransactionGuard.Difference(expected, current);
        if (difference.Length != 0)
        {
            await OperationOutput.TraceTextAsync(context, difference, "plan-difference");
            throw new IOException($"APT plan {input.PlanId} changed on this machine. Generate a new preview and request approval again.\n" + difference);
        }

        if (expected.Length == 0) return new OperationResult("No package changes were planned.");
        var hook = agent.AptTransactionHook ?? throw new NotSupportedException("This Agent cannot verify the final APT transaction.");
        var variables = new Dictionary<string, string>(AptLocale)
        {
            ["DEBIAN_FRONTEND"] = "noninteractive",
            ["NEEDRESTART_MODE"] = "l",
            [AptTransactionGuard.ExpectedEnvironment] = JsonSerializer.Serialize(expected),
            [AptTransactionGuard.ArchitectureEnvironment] = architecture
        };
        var actual = await OperationOutput.CommandAsync(context, agent, "apt-get",
            ["--yes", "-o", "Dpkg::Use-Pty=0", "-o", "Dpkg::Options::=--force-confold",
                "-o", $"DPkg::Tools::Options::{hook.Executable}::Version=3", "-o", $"DPkg::Pre-Install-Pkgs::={hook.Command}",
                .. Arguments(plan)],
            token, change: true, seconds: 1800, variables: variables, allowedExitCodes: [0, 100]);
        if (actual.ExitCode != 0 && actual.Error.Split('\n').Any(line => line.StartsWith(AptTransactionGuard.Rejected, StringComparison.Ordinal)))
            throw new IOException($"APT plan {input.PlanId} could not be verified under the package manager lock. See the workflow trace.");

        if (actual.ExitCode != 0 || !actual.Error.Split('\n').Contains(AptTransactionGuard.Confirmed))
        {
            await context.TraceAsync("APT did not confirm the approved transaction. Verify its effects before retrying.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }

        return await OperationOutput.ReportAsync(context, "Approved APT package changes completed.",
            new { Plan = input.PlanId, Packages = expected.Length,
                RebootRequired = OperationOutput.Host(agent).Exists("/var/run/reboot-required") });
    }

    [GeneratedRegex(@"\A(?<action>Inst|Conf|Remv|Purg) (?<package>[a-z0-9][a-z0-9+.-]*(?::[a-z0-9-]+)?)(?: \[(?<before>[^\s\]]+)\])?(?: \((?<after>[^\s)]+))?(?:[ \r]|\z)", RegexOptions.CultureInvariant)]
    private static partial Regex SimulationLine();

    [GeneratedRegex(@"^ *(?:(?<upgrading>[0-9]+) upgraded, (?<installing>[0-9]+) newly installed, (?<removing>[0-9]+) to remove|Upgrading: (?<upgrading>[0-9]+), Installing: (?<installing>[0-9]+), Removing: (?<removing>[0-9]+))", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex SimulationSummary();
}
