using System.Globalization;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class SystemOperations
{
    public static async Task<OperationResult> SystemInfoAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var operatingSystem = await host.ReadAsync("/etc/os-release", token, 65536);
        var kernel = await host.ReadAsync("/proc/sys/kernel/osrelease", token, 65536);
        var clock = await OperationOutput.CommandAsync(context, agent, "timedatectl", ["show", "--property=Timezone", "--property=NTPSynchronized", "--property=NTP", "--property=LocalRTC"], token, traceOutput: false);
        return await OperationOutput.ReportAsync(context, "System and clock state collected.", new { OsRelease = operatingSystem, Kernel = kernel.Trim(), Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), At = DateTimeOffset.UtcNow, Clock = clock.Output });
    }
    public static async Task<OperationResult> SystemUptimeAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var uptime = double.Parse((await host.ReadAsync("/proc/uptime", token)).Split(' ')[0], CultureInfo.InvariantCulture);
        var boot = (await host.ReadAsync("/proc/sys/kernel/random/boot_id", token)).Trim();
        return await OperationOutput.ReportAsync(context, "Boot identity and uptime collected.", new { BootId = boot, UptimeSeconds = uptime, ApproximateBootTime = DateTimeOffset.UtcNow.AddSeconds(-uptime), At = DateTimeOffset.UtcNow });
    }
    public static async Task<OperationResult> SystemctlListAsync(MachineWorkflowContext context, ServiceListInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); List<string> args = ["list-units", "--type=service", "--all", "--no-pager", "--no-legend", "--plain"];
        if (input.FailedOnly) args.Add("--state=failed");
        var result = await OperationOutput.CommandAsync(context, agent, "systemctl", args, token, traceOutput: false);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return await OperationOutput.ReportAsync(context, "Systemd service states collected.", new { Services = lines.Take(input.Limit).ToArray(), TotalObserved = lines.Length }, lines.Length <= input.Limit);
    }
    public static Task<OperationResult> SystemctlStartAsync(MachineWorkflowContext context, ServiceInput input, AgentEnvironment agent, CancellationToken token) => ChangeServiceAsync(context, input, agent, "start", token);
    public static Task<OperationResult> SystemctlStopAsync(MachineWorkflowContext context, ServiceInput input, AgentEnvironment agent, CancellationToken token) => ChangeServiceAsync(context, input, agent, "stop", token);
    private static async Task<OperationResult> ChangeServiceAsync(MachineWorkflowContext context, ServiceInput input, AgentEnvironment agent, string action, CancellationToken token)
    {
        input.Validate();
        if (input.Service == "dms-agent.service") throw new ArgumentException("DMS Agent lifecycle requires its dedicated workflow; stopping it would lose confirmation.");
        await OperationOutput.CommandAsync(context, agent, "systemctl", [action, "--", input.Service], token, change: true, seconds: 120);
        var state = await OperationOutput.CommandAsync(context, agent, "systemctl", ["show", "--property=ActiveState", "--property=SubState", "--property=Result", "--property=Restart", "--property=TriggeredBy", "--", input.Service], token, traceOutput: false);
        var expected = action == "start" ? state.Output.Contains("ActiveState=active\n", StringComparison.Ordinal) : state.Output.Contains("ActiveState=inactive\n", StringComparison.Ordinal);
        return await OperationOutput.ReportAsync(context, expected ? "Service reached its requested state." : "Service command completed, but the expected state was not confirmed.", new { input.Service, Action = action, State = state.Output }, expected);
    }
    public static List<string> JournalArguments(DateTimeOffset? since, DateTimeOffset? until, int limit)
    {
        var end = until ?? DateTimeOffset.UtcNow; var start = since ?? end.AddHours(-24);
        OperationValidation.Window(start, end); OperationValidation.Limit(limit, 10000);
        return ["--no-pager", "--output=json", "--lines=" + limit,
            "--since=" + start.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            "--until=" + end.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)];
    }
    public static async Task<OperationResult> SystemJournalAsync(MachineWorkflowContext context, JournalInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var args = JournalArguments(input.Since, input.Until, input.Limit);
        if (input.Service != null) args.Add("--unit=" + input.Service);
        if (input.Boot != null) args.Add("--boot=" + input.Boot);
        if (input.Level != null) args.Add("--priority=" + input.Level);
        await OperationOutput.CommandAsync(context, agent, "journalctl", args, token);
        return new OperationResult("Bounded journal window collected; secrets contained in application log messages remain subject to its approval.");
    }
    public static async Task<OperationResult> SystemTimersAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "systemctl", ["list-timers", "--all", "--no-pager", "--plain"], token);
        var host = OperationOutput.Host(agent); List<object> cron = [];
        foreach (var path in new[] { "/etc/crontab", "/etc/anacrontab" })
            if (host.Exists(path)) cron.Add(new { Path = path, Content = await host.ReadAsync(path, token, 262144) });
        foreach (var directory in new[] { "/etc/cron.d", "/var/spool/cron/crontabs" })
        {
            if (!Directory.Exists(host.FilePath(directory))) continue;
            foreach (var path in Directory.EnumerateFiles(host.FilePath(directory)).Take(200))
                cron.Add(new { Path = directory + "/" + Path.GetFileName(path), Content = await host.ReadAsync(directory + "/" + Path.GetFileName(path), token, 262144) });
        }
        return await OperationOutput.ReportAsync(context, "Systemd timers and readable cron schedules collected.", cron);
    }
    public static async Task<OperationResult> AptInspectAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "dpkg-query", ["--show", "--showformat=${binary:Package}\t${Version}\t${db:Status-Abbrev}\n"], token);
        await OperationOutput.CommandAsync(context, agent, "apt-cache", ["policy"], token);
        await OperationOutput.CommandAsync(context, agent, "apt-mark", ["showhold"], token);
        await OperationOutput.CommandAsync(context, agent, "apt", ["list", "--upgradable"], token);
        var check = await OperationOutput.CommandAsync(context, agent, "dpkg", ["--audit"], token, traceOutput: false);
        return await OperationOutput.ReportAsync(context, "Package versions, sources, held packages and package health collected; package lists were not refreshed.", new { Audit = check.Output, AuditErrors = check.Error }, string.IsNullOrWhiteSpace(check.Output));
    }
    public static Task<OperationResult> AptInstallAsync(MachineWorkflowContext context, PackageInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); return PackagesAsync(context, input.Packages, false, agent, token);
    }
    public static Task<OperationResult> AptUpgradeAsync(MachineWorkflowContext context, PackageUpgradeInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); return PackagesAsync(context, input.Packages, true, agent, token);
    }
    private static async Task<OperationResult> PackagesAsync(MachineWorkflowContext context, string[] packages, bool upgrade, AgentEnvironment agent, CancellationToken token)
    {
        List<string> planArguments = ["--simulate", "--no-install-recommends", "install"];
        if (upgrade) planArguments.Add("--only-upgrade"); planArguments.Add("--"); planArguments.AddRange(packages);
        var plan = await OperationOutput.CommandAsync(context, agent, "apt-get", planArguments, token, traceOutput: false);
        if (plan.Output.Split('\n').Any(l => l.StartsWith("Remv ", StringComparison.Ordinal))) throw new IOException("This package plan would remove packages; removal was not approved.");
        var approved = packages.Select(p => p.Split('=')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var line in plan.Output.Split('\n').Where(l => l.StartsWith("Inst ", StringComparison.Ordinal)))
        {
            var name = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
            if (!approved.Contains(name)) throw new IOException($"The plan also changes '{name}'. Include its exact name/version in a new approval.");
            if (upgrade)
            {
                var match = AptVersion().Match(line);
                if (!match.Success || !packages.Contains(name + "=" + match.Groups[1].Value, StringComparer.Ordinal)) throw new IOException("The package plan differs from the approved target versions.");
            }
        }
        await OperationOutput.TraceTextAsync(context, plan.Output, "plan");
        List<string> args = ["--yes", "--no-install-recommends", "install"];
        if (upgrade) args.Add("--only-upgrade"); args.Add("--"); args.AddRange(packages);
        await OperationOutput.CommandAsync(context, agent, "apt-get", args, token, change: true, seconds: 1800,
            variables: new Dictionary<string, string> { ["DEBIAN_FRONTEND"] = "noninteractive", ["NEEDRESTART_MODE"] = "l" });
        return await OperationOutput.ReportAsync(context, "Approved package operation completed.", new { Packages = packages, RebootRequired = OperationOutput.Host(agent).Exists("/var/run/reboot-required") });
    }
    public static Task<OperationResult> SystemRebootAsync(MachineWorkflowContext context, RebootInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        return (agent.Reboot ?? throw new NotSupportedException("Reboot requires the installed Agent with durable boot confirmation."))(context, input, token);
    }
    public static async Task<OperationResult> SystemExecuteScriptAsync(MachineWorkflowContext context, ScriptInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var script = await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, 262144, token);
        await context.TraceAsync("Executing the script text frozen in this VeryHigh-risk approval. No automatic replay after an uncertain outcome.");
        await OperationOutput.CommandAsync(context, agent, input.Language == "bash" ? "bash" : "python3",
            input.Language == "bash" ? ["--noprofile", "--norc", "-s"] : ["-I", "-"], token, change: true,
            seconds: input.TimeoutSeconds, stdin: script, variables: new Dictionary<string, string> { ["BASH_ENV"] = "", ["ENV"] = "", ["PYTHONPATH"] = "" });
        return new OperationResult("Approved script exited successfully.");
    }
    [GeneratedRegex(@"\(([^\s()]+)")]
    private static partial Regex AptVersion();
}
