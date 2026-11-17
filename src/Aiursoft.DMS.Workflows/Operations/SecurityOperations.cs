using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class SecurityOperations
{
    public static async Task<OperationResult> SecureBootAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent); var directory = host.FilePath("/sys/firmware/efi/efivars");
        if (!Directory.Exists(directory)) return await OperationOutput.ReportAsync(context, "UEFI Secure Boot state is unavailable in this environment.", new { State = "Unknown", Enabled = (bool?)null, Reason = "EFI variables are not exposed; this does not prove a physical host has Secure Boot disabled." }, false);
        var files = Directory.GetFiles(directory, "SecureBoot-*");
        if (files.Length != 1) return await OperationOutput.ReportAsync(context, "Secure Boot variable was not uniquely available.", new { State = "Unknown", Enabled = (bool?)null }, false);
        var value = await File.ReadAllBytesAsync(files[0], token);
        if (value.Length != 5 || value[4] > 1) throw new InvalidDataException("Invalid UEFI SecureBoot variable.");
        return await OperationOutput.ReportAsync(context, "UEFI Secure Boot variable read.", new { State = "Observed", Enabled = value[4] == 1, Evidence = Path.GetFileName(files[0]) });
    }
    public static async Task<OperationResult> DiskEncryptionAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var result = await OperationOutput.CommandAsync(context, agent, "lsblk", ["--json", "--paths", "--output", "NAME,TYPE,FSTYPE,MOUNTPOINTS"], token, traceOutput: false);
        using var document = JsonDocument.Parse(result.Output); List<(string Mount, string Device, bool Encrypted, string[] Chain)> mounts = [];
        void Visit(JsonElement device, string[] ancestors, bool encrypted)
        {
            var name = device.GetProperty("name").GetString() ?? "unknown";
            var type = device.GetProperty("type").GetString(); var chain = ancestors.Append(name).ToArray();
            encrypted |= type == "crypt";
            if (device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
                foreach (var point in points.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String))
                    mounts.Add((point.GetString()!, name, encrypted, chain));
            if (device.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) Visit(child, chain, encrypted);
        }
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray()) Visit(device, [], false);
        var rows = mounts.GroupBy(m => (m.Mount, m.Device)).Select(g => new
        {
            g.Key.Mount, g.Key.Device, Encrypted = g.All(m => m.Encrypted),
            EvidenceChains = g.Select(m => m.Chain).ToArray(), Mechanism = g.Any(m => m.Encrypted) ? "dm-crypt device ancestry" : "No dm-crypt ancestor observed"
        }).ToArray();
        return await OperationOutput.ReportAsync(context, "Encryption ancestry collected for visible block-backed mounts; remote/hardware encryption is not inferred.", new { Mounts = rows, RootObserved = rows.Any(r => r.Mount == "/") }, rows.Any(r => r.Mount == "/"));
    }
    public static async Task<OperationResult> SshAuditAsync(MachineWorkflowContext context, SshAuditInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var result = await OperationOutput.CommandAsync(context, agent, "sshd", ["-T", "-C", $"user={input.User},addr={input.Address},host={input.Host}"], token, traceOutput: false);
        var keys = new[] { "passwordauthentication", "kbdinteractiveauthentication", "permitrootlogin", "pubkeyauthentication", "trustedusercakeys", "authorizedprincipalsfile", "authenticationmethods", "authorizedkeysfile", "allowusers", "allowgroups", "denyusers", "denygroups", "usepam" }.ToHashSet(StringComparer.Ordinal);
        var settings = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ', 2)).Where(p => p.Length == 2 && keys.Contains(p[0])).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        return await OperationOutput.ReportAsync(context, "Effective SSH configuration collected for the explicit Match context.", new
        {
            Context = input, Settings = settings,
            PasswordDisabled = settings.GetValueOrDefault("passwordauthentication") == "no" && settings.GetValueOrDefault("kbdinteractiveauthentication") == "no",
            RootDisabled = settings.GetValueOrDefault("permitrootlogin") == "no",
            PublicKeysEnabled = settings.GetValueOrDefault("pubkeyauthentication") == "yes",
            CertificateCaConfigured = settings.TryGetValue("trustedusercakeys", out var ca) && ca != "none" && ca.Length > 0
        });
    }
    public static async Task<OperationResult> Fail2banStatusAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent); NativeResult result;
        try { result = await host.RunAsync(new NativeCommand("fail2ban-client", ["status"]), token); }
        catch (NotSupportedException) { return await OperationOutput.ReportAsync(context, "Fail2ban is not installed.", new { Installed = false, Running = false }); }
        if (result.ExitCode != 0) return await OperationOutput.ReportAsync(context, "Fail2ban client is installed, but the daemon did not respond successfully.", new { Installed = true, Running = false, result.ExitCode }, false);
        var line = result.Output.Split('\n').FirstOrDefault(l => l.Contains("Jail list:", StringComparison.Ordinal));
        var names = line == null ? [] : line[(line.IndexOf("Jail list:", StringComparison.Ordinal) + 10)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<object> jails = [];
        foreach (var name in names.Take(100))
        {
            OperationValidation.Name(name);
            var jail = await host.RunAsync(new NativeCommand("fail2ban-client", ["status", name]), token);
            if (jail.ExitCode != 0) await context.WarningAsync("A configured Fail2ban jail could not be read: " + name);
            jails.Add(new { Name = name, jail.ExitCode, State = jail.Output });
        }
        if (names.Length > 100) await context.WarningAsync("Only the first 100 configured Fail2ban jails were collected.");
        return await OperationOutput.ReportAsync(context, "Fail2ban status, jail counts and current bans collected.", new { Installed = true, Running = true, Jails = jails }, !context.HasWarnings);
    }
    public static async Task<OperationResult> CleanTrafficStatusAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var installed = host.Exists("/etc/ufw/after.init") && host.Exists("/etc/cron.daily/auto-blacklist-update");
        if (!installed) return await OperationOutput.ReportAsync(context, "Clean Traffic installation hooks were not found.", new { Installed = false, Running = (bool?)null });
        var script = await host.ReadAsync("/etc/cron.daily/auto-blacklist-update", token, 262144);
        var sources = SourceUrl().Matches(script).Select(m => m.Value).Distinct(StringComparer.Ordinal).Take(50).ToArray();
        List<object> sets = []; var active = true;
        foreach (var name in new[] { "ufw-blocklist-ipsum", "ufw-blocklist-ipsum-ipv6" })
        {
            var result = await host.RunAsync(new NativeCommand("ipset", ["list", name, "-terse"]), token);
            sets.Add(new { Name = name, result.ExitCode, Metadata = result.Output }); active &= result.ExitCode == 0;
        }
        List<object> counters = [];
        foreach (var tool in new[] { "iptables", "ip6tables" })
            foreach (var chain in new[] { "ufw-blocklist-input", "ufw-blocklist-forward" })
            {
                var result = await host.RunAsync(new NativeCommand(tool, ["--list", chain, "--numeric", "--verbose", "--exact"]), token);
                active &= result.ExitCode == 0;
                counters.Add(new { Tool = tool, Chain = chain, result.ExitCode, Counters = result.Output });
            }
        List<object> attachments = [];
        foreach (var (tool, set) in new[] { ("iptables", "ufw-blocklist-ipsum"), ("ip6tables", "ufw-blocklist-ipsum-ipv6") })
            foreach (var (parent, target) in new[] { ("INPUT", "ufw-blocklist-input"), ("FORWARD", "ufw-blocklist-forward") })
            {
                var result = await host.RunAsync(new NativeCommand(tool, ["--list-rules", parent]), token);
                var attached = result.ExitCode == 0 && result.Output.Split('\n').Any(line => line.Contains("-j " + target, StringComparison.Ordinal)
                    && line.Contains("--match-set " + set + " src", StringComparison.Ordinal));
                active &= attached;
                attachments.Add(new { Tool = tool, Parent = parent, Target = target, Attached = attached });
            }
        var args = SystemOperations.JournalArguments(null, null, 200); args.Add("--dmesg");
        var events = await host.RunAsync(new NativeCommand("journalctl", args), token);
        var blocked = events.Output.Split('\n').Where(l => l.Contains("UFW BLOCKLIST", StringComparison.OrdinalIgnoreCase)).ToArray();
        return await OperationOutput.ReportAsync(context, "Clean Traffic hooks, sources, in-memory sets and available block evidence collected.", new
        {
            Installed = true, Running = active, Sources = sources, Sets = sets, Counters = counters, Attachments = attachments, RecentLoggedBlocks = blocked,
            UpdateScriptModifiedAt = File.GetLastWriteTimeUtc(host.FilePath("/etc/cron.daily/auto-blacklist-update")),
            LastSuccessfulRefresh = "Unknown unless a refresh event is present; script modification time is not a refresh timestamp.",
            Note = "Aggregate drop counters do not identify every blocked IP. An empty log does not prove no packets were blocked."
        }, active);
    }
    public static async Task<OperationResult> AuthEventsAsync(MachineWorkflowContext context, EventWindowInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var args = SystemOperations.JournalArguments(input.Since, input.Until, input.Limit);
        args.AddRange(["SYSLOG_IDENTIFIER=sshd", "SYSLOG_IDENTIFIER=sudo", "SYSLOG_IDENTIFIER=su", "SYSLOG_IDENTIFIER=login"]);
        await OperationOutput.CommandAsync(context, agent, "journalctl", args, token);
        return new OperationResult("Bounded login, authentication failure and privilege-escalation events collected.");
    }
    public static async Task<OperationResult> AccountsAuditAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        var host = OperationOutput.Host(agent);
        var users = (await host.ReadAsync("/etc/passwd", token)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(':')).Where(p => p.Length == 7).Select(p => new
        {
            User = p[0], Uid = int.Parse(p[2]), Gid = int.Parse(p[3]), Home = p[5], Shell = p[6],
            LoginShell = !p[6].EndsWith("/nologin", StringComparison.Ordinal) && !p[6].EndsWith("/false", StringComparison.Ordinal)
        }).Where(u => u.Uid == 0 || u.LoginShell).ToArray();
        List<object> keys = [];
        foreach (var user in users.Take(1000))
        {
            if (!Path.IsPathRooted(user.Home)) continue;
            var path = host.FilePath(Path.Combine(user.Home, ".ssh", "authorized_keys"));
            if (File.Exists(path)) keys.Add(new { user.User, Metadata = SafeOperationPaths.Metadata(path) });
        }
        var groups = (await host.ReadAsync("/etc/group", token)).Split('\n').Where(l => l.StartsWith("sudo:", StringComparison.Ordinal) || l.StartsWith("wheel:", StringComparison.Ordinal)).ToArray();
        List<object> sudoers = [];
        var paths = new List<string> { "/etc/sudoers" };
        if (Directory.Exists(host.FilePath("/etc/sudoers.d"))) paths.AddRange(Directory.EnumerateFiles(host.FilePath("/etc/sudoers.d")).Take(100).Select(p => "/etc/sudoers.d/" + Path.GetFileName(p)));
        foreach (var path in paths)
            if (host.Exists(path)) sudoers.Add(new { Path = path, Rules = (await host.ReadAsync(path, token, 262144)).Split('\n').Where(l => !string.IsNullOrWhiteSpace(l) && (!l.TrimStart().StartsWith('#') || l.TrimStart().StartsWith("#include", StringComparison.Ordinal))).ToArray() });
        return await OperationOutput.ReportAsync(context, "Login accounts, UID0, sudo rules and authorized-key file metadata collected; no private key or password hash is read.", new { Users = users, PrivilegedGroups = groups, Sudoers = sudoers, AuthorizedKeyFiles = keys });
    }
    public sealed record SecurityBaseline(string Version, bool RequireSecureBoot = true, string[]? EncryptedMounts = null,
        bool RequireCertificateCa = true, bool RequireFail2ban = true, bool RequireCleanTraffic = true, string? BcacheBaselinePath = null);
    public static async Task<OperationResult> BaselineAuditAsync(MachineWorkflowContext context, BaselineInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var reports = new Dictionary<string, OperationResult>();
        async Task Collect<T>(string key, MachineWorkflowDefinition<T, OperationResult> definition, T value)
        {
            try { reports[key] = await context.CallAsync(definition, value, token); }
            catch (ChildWorkflowException) { reports[key] = new OperationResult("Collection failed; inspect child trace.", false); await context.WarningAsync(key + " could not be collected."); }
        }
        await Collect("SecureBoot", OperationsWorkflows.SecuritySecureBoot, new EmptyInput());
        await Collect("Encryption", OperationsWorkflows.SecurityDiskEncryption, new EmptyInput());
        await Collect("Ssh", OperationsWorkflows.SecuritySshAudit, new SshAuditInput());
        await Collect("Fail2ban", OperationsWorkflows.SecurityFail2banStatus, new EmptyInput());
        await Collect("CleanTraffic", OperationsWorkflows.SecurityCleanTrafficStatus, new EmptyInput());
        await Collect("Firewall", OperationsWorkflows.NetworkingFirewallInfo, new EmptyInput());
        if (input.BaselinePath == null) return await OperationOutput.ReportAsync(context, "Security observations collected; compliance is unknown without a versioned baseline.", new { Status = "Unknown", Observations = reports }, false);
        SafeOperationPaths.Check(OperationOutput.Host(agent).FilePath(input.BaselinePath));
        var baseline = JsonSerializer.Deserialize<SecurityBaseline>(await OperationOutput.Host(agent).ReadAsync(input.BaselinePath, token)) ?? throw new InvalidDataException("Invalid security baseline.");
        if (string.IsNullOrWhiteSpace(baseline.Version)) throw new InvalidDataException("Security baseline must identify its version.");
        bool? Fact(string key, string property) => reports.TryGetValue(key, out var report) && report.Facts is { } facts && facts.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
        var checks = new Dictionary<string, bool?>
        {
            ["SecureBoot"] = !baseline.RequireSecureBoot ? true : Fact("SecureBoot", "Enabled"),
            ["SshPasswordDisabled"] = Fact("Ssh", "PasswordDisabled"), ["SshRootDisabled"] = Fact("Ssh", "RootDisabled"),
            ["SshPublicKeys"] = Fact("Ssh", "PublicKeysEnabled"),
            ["SshCertificateCa"] = !baseline.RequireCertificateCa ? true : Fact("Ssh", "CertificateCaConfigured"),
            ["Fail2ban"] = !baseline.RequireFail2ban ? true : Fact("Fail2ban", "Running"),
            ["CleanTraffic"] = !baseline.RequireCleanTraffic ? true : Fact("CleanTraffic", "Running")
        };
        foreach (var mount in baseline.EncryptedMounts ?? ["/"])
        {
            var facts = reports["Encryption"].Facts;
            var observed = facts?.TryGetProperty("Mounts", out var rows) == true ? rows.EnumerateArray().FirstOrDefault(r => r.GetProperty("Mount").GetString() == mount) : default;
            checks["Encrypted:" + mount] = observed.ValueKind == JsonValueKind.Object ? observed.GetProperty("Encrypted").GetBoolean() : null;
        }
        if (baseline.BcacheBaselinePath != null)
        {
            await Collect("Bcache", OperationsWorkflows.LayoutBcacheAudit, new BaselineInput(baseline.BcacheBaselinePath));
            checks["Bcache"] = reports["Bcache"].Complete;
        }
        return await OperationOutput.ReportAsync(context, "Versioned security baseline comparison completed; null checks are unknown.", new { baseline.Version, Checks = checks, Observations = reports }, checks.Values.All(v => v == true));
    }
    [GeneratedRegex("https?://[^\\s\"'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex SourceUrl();
}
