using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

public sealed partial class FrpsDeployWorkflow(FrpsDeployInput input, AgentEnvironment agent,
    Func<CancellationToken, Task<byte[]>>? download = null)
    : MachineWorkflow<FrpsDeployInput, FrpsDeployResult>(MachineWorkflows.FrpsDeploy, input)
{
    private const string Unit = "frps.service";
    private const string UnitPath = "/etc/systemd/system/frps.service";
    private const string DefaultBinary = "/usr/local/bin/frps";
    private const string DefaultConfig = "/etc/frp/frps.toml";
    private const string Release = "0.72.0";
    private const int MaxArchiveSize = 64 * 1024 * 1024;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode ExecutableMode = PrivateMode | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode UnitMode = PrivateMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private const string UnitContent = """
        [Unit]
        Description=FRP server
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=exec
        ExecStart=/usr/local/bin/frps -c /etc/frp/frps.toml
        Restart=on-failure
        RestartSec=5s

        [Install]
        WantedBy=multi-user.target
        """;

    public override async Task<FrpsDeployResult> ExecuteAsync(MachineWorkflowContext context, FrpsDeployInput input, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("FRPS deployment requires Linux with systemd.");
        if (input.FrpsToken != null) throw new InvalidDataException("The Agent requires a protected FRPS token.");
        var host = agent.Operations ?? OperationHost.Default;
        await using var deploymentLock = new FileStream(host.FilePath("/run/aiursoft-dms-frps.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var service = await host.RunAsync(new NativeCommand("systemctl", ["show", "-p", "LoadState", "-p", "ExecStart", Unit]), token);
        var isNew = service.Output.Split('\n').Any(line => line.TrimEnd() == "LoadState=not-found");
        if (!isNew && (service.ExitCode != 0 || !service.Output.Split('\n').Any(line => line.TrimEnd() == "LoadState=loaded")))
            throw new InvalidOperationException("Could not determine the state of frps.service.");

        var binary = DefaultBinary;
        var config = DefaultConfig;
        if (isNew)
        {
            if (new[] { DefaultBinary, "/usr/bin/frps", DefaultConfig, "/etc/frp/frps.ini", UnitPath, "/usr/lib/turn/turn" }
                .Any(path => host.Exists(path) || new FileInfo(host.FilePath(path)).LinkTarget != null))
                throw new InvalidOperationException("A partial or nonstandard FRPS installation exists. Adopt it as frps.service before deploying.");
            if (input.ProtectedToken == null)
                throw new InvalidOperationException("FRPS is not installed as frps.service. A nonempty --frps-token is required for installation.");
        }
        else
        {
            var entries = ExecStart().Matches(service.Output);
            if (entries.Count != 1 ||
                !string.Equals(Path.GetFileName(entries[0].Groups["binary"].Value), "frps", StringComparison.Ordinal) ||
                entries[0].Groups["binary"].Value != entries[0].Groups["argv"].Value)
                throw new InvalidOperationException("frps.service must use one absolute FRPS executable and an absolute configuration path.");
            binary = entries[0].Groups["binary"].Value;
            config = entries[0].Groups["config"].Value;
        }

        var binaryPath = host.FilePath(binary);
        var configPath = host.FilePath(config);
        var unitPath = host.FilePath(UnitPath);
        if (new FileInfo(binaryPath).LinkTarget != null || new FileInfo(configPath).LinkTarget != null)
            throw new InvalidOperationException("The FRPS executable and configuration must be regular files.");
        var oldVersion = isNew ? null : await ReadVersionAsync(host, binaryPath, token);
        if (oldVersion != null && System.Version.Parse(oldVersion) > System.Version.Parse(Release))
            throw new InvalidOperationException("The installed FRPS version is newer than the DMS release.");
        var currentConfig = isNew ? null : await host.ReadAsync(config, token);
        var wasActive = !isNew && await IsActiveAsync(host, token);
        if (!isNew && oldVersion == Release && input.ProtectedToken == null && wasActive)
            return new FrpsDeployResult(oldVersion, Release, false, false);

        var updatedConfig = default(string);
        if (input.ProtectedToken != null)
        {
            if (!isNew && !config.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FRPS token rotation requires TOML configuration.");
            var readToken = agent.GetFrpsDeployToken ?? throw new InvalidOperationException("The Agent cannot fetch the approved FRPS token.");
            var newToken = await readToken(context.Id, token);
            new FrpsDeployInput(newToken).Validate();
            if (newToken.Length == 0) throw new InvalidOperationException("The approved FRPS token is empty.");
            updatedConfig = isNew
                ? $"bindPort = 7000\nkcpBindPort = 7000\nauth.method = \"token\"\nauth.token = {QuoteToken(newToken)}\n"
                : RotateToken(currentConfig!, newToken);
        }

        var updateBinary = oldVersion != Release;
        var nextBinary = updateBinary ? await (download ?? DownloadReleaseAsync)(token) : null;
        var previousBinary = !isNew && updateBinary ? await File.ReadAllBytesAsync(binaryPath, token) : null;
        var previousConfig = !isNew && updatedConfig != null ? await File.ReadAllBytesAsync(configPath, token) : null;
        var binaryMode = isNew ? ExecutableMode : File.GetUnixFileMode(binaryPath);
        var configMode = isNew ? PrivateMode : File.GetUnixFileMode(configPath);
        var staging = Directory.CreateTempSubdirectory("dms-frps-");
        try
        {
            File.SetUnixFileMode(staging.FullName, PrivateMode | UnixFileMode.UserExecute);
            var candidate = binaryPath;
            if (nextBinary != null)
            {
                candidate = Path.Combine(staging.FullName, "frps");
                await WriteAsync(candidate, nextBinary, ExecutableMode, token);
                if (await ReadVersionAsync(host, candidate, token) != Release)
                    throw new InvalidDataException("The FRPS release executable reported an unexpected version.");
            }

            var candidateConfig = configPath;
            if (updatedConfig != null)
            {
                candidateConfig = Path.Combine(staging.FullName, Path.GetFileName(config));
                await WriteAsync(candidateConfig, System.Text.Encoding.UTF8.GetBytes(updatedConfig), PrivateMode, token);
            }
            await RequireAsync(host, candidate, ["verify", "-c", candidateConfig], "The FRPS release rejected the configuration.", token);

            var binaryChanged = false;
            var configChanged = false;
            var unitChanged = false;
            var serviceTouched = false;
            try
            {
                if (nextBinary != null)
                {
                    await ReplaceAsync(binaryPath, nextBinary, binaryMode, token);
                    binaryChanged = true;
                }
                if (updatedConfig != null)
                {
                    await ReplaceAsync(configPath, System.Text.Encoding.UTF8.GetBytes(updatedConfig), configMode, token);
                    configChanged = true;
                }
                if (isNew)
                {
                    await ReplaceAsync(unitPath, System.Text.Encoding.UTF8.GetBytes(UnitContent + "\n"), UnitMode, token);
                    unitChanged = true;
                    await SystemctlAsync(host, ["daemon-reload"], token);
                }

                serviceTouched = true;
                await SystemctlAsync(host, isNew ? ["enable", "--now", Unit] : ["restart", Unit], token);
                await CheckRunningAsync(host, token);
                return new FrpsDeployResult(oldVersion, Release, isNew, updatedConfig != null);
            }
            catch
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    if (isNew && serviceTouched)
                        await SystemctlAsync(host, ["disable", "--now", Unit], recovery.Token);
                    if (unitChanged)
                    {
                        File.Delete(unitPath);
                        await SystemctlAsync(host, ["daemon-reload"], recovery.Token);
                    }
                    if (configChanged)
                    {
                        if (isNew) File.Delete(configPath);
                        else await ReplaceAsync(configPath, previousConfig!, configMode, recovery.Token);
                    }
                    if (binaryChanged)
                    {
                        if (isNew) File.Delete(binaryPath);
                        else await ReplaceAsync(binaryPath, previousBinary!, binaryMode, recovery.Token);
                    }
                    if (!isNew && serviceTouched)
                    {
                        await SystemctlAsync(host, [wasActive ? "restart" : "stop", Unit], recovery.Token);
                        if (wasActive) await CheckRunningAsync(host, recovery.Token);
                    }
                }
                catch
                {
                    await context.TraceAsync("FRPS rollback could not be confirmed.", "frps");
                    throw new WorkflowNeedsVerificationException(context.Id);
                }
                throw;
            }
        }
        finally { staging.Delete(recursive: true); }
    }

    private static async Task CheckRunningAsync(OperationHost host, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(2), token);
        if (!await IsActiveAsync(host, token)) throw new InvalidOperationException("FRPS did not remain active after deployment.");
    }

    private static async Task<bool> IsActiveAsync(OperationHost host, CancellationToken token) =>
        (await host.RunAsync(new NativeCommand("systemctl", ["is-active", "--quiet", Unit]), token)).ExitCode == 0;

    private static Task SystemctlAsync(OperationHost host, IReadOnlyList<string> arguments, CancellationToken token) =>
        RequireAsync(host, "systemctl", arguments, "Systemd could not apply the FRPS change.", token);

    private static async Task RequireAsync(OperationHost host, string path, IReadOnlyList<string> arguments, string error, CancellationToken token)
    {
        // FRPS configuration errors can contain the offending line, including credentials.
        var result = await host.RunAsync(new NativeCommand(path, arguments, MaxCharacters: 65536), token);
        if (result.ExitCode != 0) throw new InvalidOperationException(error);
    }

    private static async Task<string> ReadVersionAsync(OperationHost host, string binary, CancellationToken token)
    {
        var result = await host.RunAsync(new NativeCommand(binary, ["--version"]), token);
        var version = result.Output.Trim();
        if (result.ExitCode != 0 || !VersionNumber().IsMatch(version))
            throw new InvalidDataException("The FRPS executable did not report a supported version number.");
        return version;
    }

    private static string QuoteToken(string token) => "\"" + token.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string RotateToken(string config, string token)
    {
        if (UnsupportedAuth().IsMatch(config))
            throw new InvalidDataException("This FRPS configuration uses non-token or external authentication.");
        var settings = TokenSetting().Matches(config);
        if (settings.Count != 1)
            throw new InvalidDataException("Expected exactly one inline auth.token setting in the existing FRPS configuration.");
        var setting = settings[0];
        var value = setting.Groups["value"];
        return config[..value.Index] + QuoteToken(token) + config[(value.Index + value.Length)..];
    }

    [SupportedOSPlatform("linux")]
    private static async Task ReplaceAsync(string path, byte[] bytes, UnixFileMode mode, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var candidate = path + ".dms-" + Guid.NewGuid().ToString("N");
        try
        {
            await WriteAsync(candidate, bytes, mode, token);
            File.Move(candidate, path, overwrite: true);
        }
        finally { File.Delete(candidate); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteAsync(string path, byte[] bytes, UnixFileMode mode, CancellationToken token)
    {
        await using var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = mode
        });
        await file.WriteAsync(bytes, token);
        await file.FlushAsync(token);
    }

    private static async Task<byte[]> DownloadReleaseAsync(CancellationToken token)
    {
        var (architecture, hash) = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ("amd64", "daf719b32ca2778770bb92955e231a07414d754378b9af328aa2642862479804"),
            Architecture.Arm64 => ("arm64", "02180b857480302fd934e9ddbd50fe2f73229f99062f88756abc6de9df5e95cf"),
            _ => throw new PlatformNotSupportedException("DMS provides FRPS releases for Linux x64 and ARM64.")
        };
        var name = $"frp_{Release}_linux_{architecture}";
        using var response = await Http.GetAsync($"https://github.com/fatedier/frp/releases/download/v{Release}/{name}.tar.gz",
            HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        using var content = await response.Content.ReadAsStreamAsync(token);
        using var archive = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await content.ReadAsync(buffer, token)) > 0)
        {
            if (archive.Length + count > MaxArchiveSize) throw new InvalidDataException("The FRPS archive exceeded the size limit.");
            await archive.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (!SHA256.HashData(archive.GetBuffer().AsSpan(0, (int)archive.Length)).SequenceEqual(Convert.FromHexString(hash)))
            throw new InvalidDataException("The FRPS archive did not match the pinned SHA-256.");

        archive.Position = 0;
        using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: token) is { } entry)
        {
            if (entry.Name != name + "/frps") continue;
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) ||
                entry.DataStream == null || entry.Length is <= 0 or > MaxArchiveSize)
                throw new InvalidDataException("The FRPS archive contains an invalid executable entry.");
            var binary = new byte[(int)entry.Length];
            await entry.DataStream.ReadExactlyAsync(binary, token);
            return binary;
        }
        throw new InvalidDataException("The FRPS archive did not contain its executable.");
    }

    [GeneratedRegex(@"(?m)^ExecStart=\{ path=(?<binary>/[\w./-]+) ; argv\[\]=(?<argv>/[\w./-]+) (?:-c |--config[ =])(?<config>/[\w./-]+) ; [^\n{}]+\}\r?$")]
    private static partial Regex ExecStart();

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+\z")]
    private static partial Regex VersionNumber();

    [GeneratedRegex("""(?m)^[ \t]*(?:auth\s*\.\s*token\s*=|\[auth\][ \t]*(?:#[^\r\n]*)?\r?\n(?:(?![ \t]*\[)[^\r\n]*\r?\n)*?[ \t]*token\s*=)[ \t]*(?<value>"(?:[^"\\\r\n]|\\.)*"|'[^'\r\n]*')[ \t]*(?:#[^\r\n]*)?\r?$""")]
    private static partial Regex TokenSetting();

    [GeneratedRegex("""(?im)^[ \t]*(?:\[auth\.tokenSource\]|auth\.tokenSource(?:\.|\s*=)|(?:auth\.)?tokenSource(?:\.|\s*=)|(?:auth\.)?method\s*=\s*["'](?!token["']))""")]
    private static partial Regex UnsupportedAuth();
}
