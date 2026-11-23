using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed record UpgradeHandoff(Guid TaskId, Guid WorkflowId, Guid? ParentId, Guid InstallationId, Guid MachineId,
    AgentRelease Release, string PreviousVersion, DateTime Deadline, string Phase, string? ToolAssemblySha256 = null, string? Failure = null);

// Manual Lockbox approval or the managed Agent's schedule initiates preparation. The systemd unit survives its Agent.
public sealed class AgentUpgrade(string state, TaskJournal journal)
{
    private static readonly HttpClient Downloads = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
    private const string Destination = "/opt/aiursoft-dms-agent";
    private string Marker => Path.Combine(state, "upgrade.json");
    private static string Stage(string state, Guid id) => Path.Combine(state, "upgrades", id.ToString("N"));
    public static string Version => typeof(AgentUpgrade).Assembly.GetName().Version!.ToString();
    public static string Architecture => RuntimeInformation.ProcessArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "linux-x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "linux-arm64",
        _ => throw new InvalidOperationException("Unsupported Agent architecture.")
    };

    public async Task<AgentUpgradeResult> PrepareAsync(Guid taskId, MachineWorkflowContext context, AgentUpgradeInput input, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || !InstalledService())
            throw new InvalidOperationException("Agent upgrades require the installed Linux systemd Agent.");
        var config = AgentConfiguration.Load(state) ?? throw new InvalidDataException("Missing installed Agent identity.");
        if (config.MachineId == null || config.InstallationId != journal.InstallationId)
            throw new InvalidDataException("Agent installation identity differs.");
        if (File.Exists(Marker) && Read(Marker).Phase is "prepared" or "stopping" or "installing" or "installed")
            throw new InvalidOperationException("An earlier Agent upgrade requires verification.");
        input.Validate();
        var release = input.Releases.Single(r => r.Architecture == Architecture);
        var directory = Stage(state, context.Id);
        var handedOff = false;
        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await context.TraceAsync($"Preparing Agent {Version} -> {release.Version}; {release.Architecture}; archive {release.ArchiveSha256}.");
            using var response = await Downloads.GetAsync(config.Controller.TrimEnd('/') + "/agent/download/" + Architecture, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var archive = Path.Combine(directory, "agent.tar.gz");
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using var source = await response.Content.ReadAsStreamAsync(token);
                await source.CopyToAsync(output, token);
                output.Flush(true);
            }
            if (Hash(archive) != release.ArchiveSha256) throw new InvalidDataException("Agent release asset changed or was corrupted. Submit a new Lockbox request.");
            var payload = Path.Combine(directory, "payload");
            Directory.CreateDirectory(payload);
            // Never allow a release archive to create links or escape its new staging directory.
            using (var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
            using (var reader = new TarReader(gzip))
            {
                long extracted = 0;
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(cancellationToken: token)) != null)
                {
                    var file = Path.GetFullPath(Path.Combine(payload, entry.Name));
                    if (file == payload && entry.EntryType == TarEntryType.Directory) continue;
                    if (!file.StartsWith(payload + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                        throw new InvalidDataException("Invalid Agent archive entry.");
                    if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(file);
                    else
                    {
                        extracted = checked(extracted + entry.Length);
                        if (extracted > 1024L * 1024 * 1024) throw new InvalidDataException("Agent archive exceeds the extraction limit.");
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        await entry.ExtractToFileAsync(file, overwrite: false, cancellationToken: token);
                    }
                }
            }
            if (Hash(Path.Combine(payload, "Aiursoft.DMS.Agent.dll")) != release.AssemblySha256)
                throw new InvalidDataException("Agent assembly does not match the approved release.");
            var executable = Path.Combine(payload, "Aiursoft.DMS.Agent");
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            await RunAsync(executable, ["--help"], token);
            token.ThrowIfCancellationRequested();
            var handoff = new UpgradeHandoff(taskId, context.Id, context.ParentId, config.InstallationId, config.MachineId.Value,
                release, Version, DateTime.UtcNow.AddMinutes(10), "prepared");
            await context.TraceAsync("Update handoff persisted. Completion requires the new Agent to reconnect; cancellation cannot roll back the switch.");
            Save(Marker, handoff);
            try
            {
                await RunAsync("/usr/bin/systemd-run", ["--unit=dms-agent-upgrade-" + context.Id.ToString("N"), "--collect",
                    "--property=Type=exec", "--property=RuntimeMaxSec=300", executable, "apply-upgrade", "--state-path", state, "--id", context.Id.ToString()], CancellationToken.None);
            }
            catch { Save(Marker, handoff with { Phase = "failed" }); throw; }
            handedOff = true;
            // Returning would incorrectly complete the workflow before the service restarts.
            while (DateTime.UtcNow < handoff.Deadline.AddMinutes(5))
            {
                await Task.Delay(2000, CancellationToken.None);
                var current = Read(Marker);
                if (current.Phase == "failed") throw new IOException(current.Failure ?? "The Agent update helper failed before replacement.");
            }
            Save(Marker, handoff with { Phase = "uncertain" });
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        finally
        {
            if (!handedOff && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    public async Task ConfirmAsync()
    {
        if (!File.Exists(Marker)) return;
        var handoff = Read(Marker);
        // An automatic service restart can race the independent updater. Do not admit new work
        // while that updater may still stop this process. A late-starting helper must validate
        // the original deadline before any effect; allow its maximum five-minute runtime to end.
        // Cap malformed future deadlines too. This waits, never replays the update.
        var activationDeadline = handoff.Deadline < DateTime.UtcNow.AddMinutes(10) ? handoff.Deadline : DateTime.UtcNow.AddMinutes(10);
        var waitUntil = activationDeadline.AddMinutes(5);
        while (handoff.Phase is "prepared" or "stopping" or "installing" && DateTime.UtcNow < waitUntil)
        {
            await Task.Delay(200);
            var current = Read(Marker);
            if (current.WorkflowId != handoff.WorkflowId) throw new InvalidDataException("Agent update handoff changed during recovery.");
            handoff = current;
        }
        if (handoff.Phase is "confirmed" or "failed" or "uncertain") return;
        var task = journal.Snapshot().SingleOrDefault(t => t.Id == handoff.TaskId);
        if (task == null || task.State.Terminal()) return;
        var config = AgentConfiguration.Load(state);
        var isRoot = handoff.WorkflowId == task.Id;
        var definition = isRoot ? task.Definition : WorkflowNames.AgentUpgrade;
        var version = isRoot ? task.Version : MachineWorkflows.AgentUpgrade.Version;
        var recorded = isRoot
            ? task.Definition switch
            {
                WorkflowNames.AgentUpgrade => MachineWorkflows.AgentUpgrade.ReadInput(task.Input).Releases.Contains(handoff.Release),
                ScheduledAgentUpgradeWorkflow.DefinitionName => task.Schedule != null && task.ScheduledUpgradeRelease == handoff.Release,
                _ => false
            }
            : handoff.ParentId != null; // The durable handoff was written by the actual child context before restart.
        var matches = recorded && handoff.Phase == "installed" && handoff.Deadline > DateTime.UtcNow
            && config?.InstallationId == handoff.InstallationId && config.MachineId == handoff.MachineId
            && journal.InstallationId == handoff.InstallationId && handoff.Release.Version == Version
            && handoff.ToolAssemblySha256 != null
            && Hash(typeof(AgentUpgrade).Assembly.Location) == handoff.ToolAssemblySha256;
        await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = handoff.WorkflowId, ParentId = handoff.ParentId,
            IsTrace = true, Message = matches ? $"Agent {Version} started with the approved assembly and retained installation {journal.InstallationId}. Reporting over the authenticated Controller session."
                : "Agent update outcome requires verification. The update will not be repeated automatically." });
        if (matches)
        {
            await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = handoff.WorkflowId, ParentId = handoff.ParentId,
                Definition = definition, Version = version, State = RunState.Succeeded,
                Result = JsonSerializer.Serialize(new AgentUpgradeResult(handoff.PreviousVersion, Version, Architecture, handoff.Release.ArchiveSha256, journal.InstallationId)) });
        }
        else
        {
            await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = handoff.WorkflowId, ParentId = handoff.ParentId,
                Definition = definition, Version = version, State = RunState.NeedsVerification });
        }
        Save(Marker, handoff with { Phase = matches ? "confirmed" : "uncertain" });
        // No task is re-executed and no C# parent stack is resumed by this confirmation.
    }

    public static async Task ApplyAsync(string state, Guid id)
    {
        var marker = Path.Combine(state, "upgrade.json");
        var handoff = Read(marker);
        var payload = Path.Combine(Stage(state, id), "payload");
        var config = AgentConfiguration.Load(state) ?? throw new InvalidDataException("Missing Agent identity.");
        if (handoff.WorkflowId != id || handoff.Phase != "prepared" || handoff.Deadline <= DateTime.UtcNow
            || config.InstallationId != handoff.InstallationId || config.MachineId != handoff.MachineId
            || Version != handoff.Release.Version || Hash(typeof(AgentUpgrade).Assembly.Location) != handoff.Release.AssemblySha256
            || Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/') != payload)
            throw new InvalidDataException("Invalid or expired Agent update handoff.");
        // Both the package and its manifest are covered by the original approved archive hash.
        // A native helper keeps older installed Agents able to migrate without a second bootstrap.
        var stopped = false;
        try
        {
            if (Hash(Path.Combine(Stage(state, id), "agent.tar.gz")) != handoff.Release.ArchiveSha256)
                throw new InvalidDataException("Agent release archive differs from the approved release.");
            var tool = await InstallToolAsync(payload);
            if (handoff.Deadline <= DateTime.UtcNow || Read(marker).Phase != "prepared")
                throw new InvalidDataException("Agent update handoff expired or changed during preparation.");
            handoff = handoff with { ToolAssemblySha256 = tool.AssemblySha256 };
            Save(marker, handoff with { Phase = "stopping" });
            stopped = true; // Even an unconfirmed stop can have affected the running service.
            await RunAsync("/usr/bin/systemctl", ["stop", "dms-agent.service"], CancellationToken.None);
            using (var journal = new TaskJournal(Path.Combine(state, "journal")))
            {
                if (journal.InstallationId != handoff.InstallationId) throw new InvalidDataException("Installation changed during update.");
                await journal.ReportAsync(handoff.TaskId, new WorkflowEvent { WorkflowId = id, ParentId = handoff.ParentId,
                    IsTrace = true, Source = "updater", Message = "Old Agent stopped. Activating the approved Tool; task identities are preserved." });
                Save(marker, handoff with { Phase = "installing" });
                ActivateTool(tool.Directory);
                await journal.ReportAsync(handoff.TaskId, new WorkflowEvent { WorkflowId = id, ParentId = handoff.ParentId,
                    IsTrace = true, Source = "updater", Message = $"Agent {handoff.Release.Version} Tool installed. Starting the service; confirmation is still pending." });
                Save(marker, handoff with { Phase = "installed" });
            }
            await RunAsync("/usr/bin/systemctl", ["daemon-reload"], CancellationToken.None);
            await RunAsync("/usr/bin/systemctl", ["start", "dms-agent.service"], CancellationToken.None);
        }
        catch (Exception exception)
        {
            var failure = exception.Message[..Math.Min(exception.Message.Length, 2000)];
            Save(marker, handoff with { Phase = stopped ? "uncertain" : "failed", Failure = failure });
            if (stopped)
            {
                using (var journal = new TaskJournal(Path.Combine(state, "journal")))
                {
                    var task = journal.Snapshot().Single(item => item.Id == handoff.TaskId);
                    await journal.ReportAsync(handoff.TaskId, new WorkflowEvent { WorkflowId = id, ParentId = handoff.ParentId,
                        IsTrace = true, Source = "updater", Message = "Agent Tool update requires verification: " + failure });
                    await journal.ReportAsync(handoff.TaskId, new WorkflowEvent { WorkflowId = id, ParentId = handoff.ParentId,
                        Definition = id == task.Id ? task.Definition : WorkflowNames.AgentUpgrade,
                        Version = id == task.Id ? task.Version : MachineWorkflows.AgentUpgrade.Version, State = RunState.NeedsVerification });
                }
                // Restore service availability where possible, without replaying the operation.
                try
                {
                    await RunAsync("/usr/bin/systemctl", ["daemon-reload"], CancellationToken.None);
                    await RunAsync("/usr/bin/systemctl", ["start", "dms-agent.service"], CancellationToken.None);
                }
                catch (IOException) { }
            }
            throw;
        }
        // Temporary-file cleanup must not change the outcome of an already activated Agent.
        Directory.Delete(Stage(state, id), true);
    }

    private static bool InstalledService()
    {
        var directory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/');
        return directory == Destination || directory.StartsWith(Destination + "/tools/", StringComparison.Ordinal);
    }

    private sealed record ToolRelease(string PackageVersion, string PackageSha256, string AssemblySha256);
    private sealed record InstalledTool(string Directory, string AssemblySha256);

    private static async Task<InstalledTool> InstallToolAsync(string payload)
    {
        var release = JsonSerializer.Deserialize<ToolRelease>(File.ReadAllText(Path.Combine(payload, "tool.json")))
            ?? throw new InvalidDataException("Missing Agent Tool release.");
        if (!System.Version.TryParse(release.PackageVersion, out var version) || version.ToString(3) != System.Version.Parse(Version).ToString(3)
            || release.PackageSha256.Length != 64 || release.AssemblySha256.Length != 64
            || !release.PackageSha256.All(char.IsAsciiHexDigit) || !release.AssemblySha256.All(char.IsAsciiHexDigit)
            || Hash(Path.Combine(payload, "agent.nupkg")) != release.PackageSha256)
            throw new InvalidDataException("Agent Tool package differs from the approved release.");
        // The tool package is entirely bundled: install from this local feed, with no external NuGet source.
        var feed = Path.Combine(payload, "feed");
        Directory.CreateDirectory(feed);
        File.Copy(Path.Combine(payload, "agent.nupkg"), Path.Combine(feed, $"Aiursoft.DMS.Agent.{release.PackageVersion}.nupkg"), true);
        var config = Path.Combine(feed, "nuget.config");
        await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"controller\" value=\".\"/></packageSources></configuration>");
        var directory = Path.Combine(Destination, "tools", release.PackageVersion + "-" + release.PackageSha256[..12]);
        // Existing native services need the same one-time SDK preparation as the installer.
        if (!File.Exists("/usr/bin/dotnet") || !(await RunAsync("/usr/bin/dotnet", ["--list-sdks"], CancellationToken.None))
                .Split('\n').Any(line => line.StartsWith("10.", StringComparison.Ordinal)))
        {
            await RunAsync("/usr/bin/apt-get", ["update"], CancellationToken.None);
            await RunAsync("/usr/bin/apt-get", ["install", "-y", "dotnet10"], CancellationToken.None);
        }
        await RunAsync("/usr/bin/dotnet", ["tool", "update", "Aiursoft.DMS.Agent", "--version", release.PackageVersion,
            "--tool-path", directory, "--configfile", config], CancellationToken.None, Path.Combine(feed, "packages"));
        var assemblies = Directory.GetFiles(Path.Combine(directory, ".store"), "Aiursoft.DMS.Agent.dll", SearchOption.AllDirectories);
        if (assemblies.Length != 1 || Hash(assemblies[0]) != release.AssemblySha256)
            throw new InvalidDataException("Installed Agent Tool assembly differs from the approved release.");
        await RunAsync(Path.Combine(directory, "dms-agent"), ["--version"], CancellationToken.None);
        return new InstalledTool(directory, release.AssemblySha256);
    }

    private static void ActivateTool(string directory)
    {
        var executable = Path.Combine(Destination, "dms-agent");
        var next = executable + ".next";
        File.Delete(next);
        File.CreateSymbolicLink(next, Path.Combine(directory, "dms-agent"));
        File.Move(next, executable, true);
        File.Delete("/usr/local/bin/dms-agent");
        File.CreateSymbolicLink("/usr/local/bin/dms-agent", executable);
        const string unit = "/etc/systemd/system/dms-agent.service";
        File.WriteAllText(unit + ".next", """
            [Unit]
            Description=Aiursoft DMS Agent
            Wants=network-online.target
            After=network-online.target

            [Service]
            Type=simple
            User=root
            ExecStart=/opt/aiursoft-dms-agent/dms-agent run
            Restart=on-failure
            RestartSec=15
            UMask=0077

            [Install]
            WantedBy=multi-user.target
            """ + "\n");
        File.Move(unit + ".next", unit, true);
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token, string? packages = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        if (packages != null) process.StartInfo.Environment["NUGET_PACKAGES"] = packages;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var cancellation = token.Register(static state =>
        {
            var running = (Process)state!;
            try { if (!running.HasExited) running.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }, process);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var standardOutput = await output;
        var diagnostic = (await error).Trim();
        if (process.ExitCode != 0) throw new IOException($"Agent lifecycle process {Path.GetFileName(executable)} failed with exit code {process.ExitCode}: {diagnostic[..Math.Min(diagnostic.Length, 2000)]}");
        return standardOutput;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static UpgradeHandoff Read(string path) => JsonSerializer.Deserialize<UpgradeHandoff>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Invalid Agent update handoff.");
    private static void Save(string path, UpgradeHandoff handoff)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, handoff);
            stream.Flush(true);
        }
        File.Move(temporary, path, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
