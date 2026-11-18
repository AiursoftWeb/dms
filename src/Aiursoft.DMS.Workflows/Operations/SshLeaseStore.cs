using System.Globalization;
using System.Text.Json;

namespace Aiursoft.DMS.Workflows.Operations;

internal sealed record SshLease(Guid Id, int Uid, int Port, bool Ipv4Rule, bool Ipv6Rule,
    string? BootId = null, DateTimeOffset? ExpiresAtUtc = null, string? RequesterId = null)
{
    internal static string UserName(Guid id) => "dms-tmp-user-" + id.ToString("N")[..16];
    internal string User => UserName(Id);
    internal string UserId => Uid.ToString(CultureInfo.InvariantCulture);
    internal string Home => "/home/" + User;
    internal string FirewallTag => $"dms-ssh-{Id:N}";
    internal string Unit => FirewallTag + ".service";
    internal string AuthorizedKeyPath => $"/etc/ssh/{FirewallTag}.pub";
    internal string ConfigPath => $"/var/lib/aiursoft-dms-ssh/{FirewallTag}.conf";

    public void Validate()
    {
        if (Id == Guid.Empty || Uid is < 1000 or >= 65534 || Port is < 1 or > 65535)
            throw new InvalidDataException("Invalid local SSH lease ID, UID or port.");
    }
}

internal sealed record SshLeaseState(SshLease? Lease, bool Healthy);
internal sealed record SshRevocation(Guid? LeaseId, bool Complete);
internal sealed record SshOpening(DateTimeOffset? ExpiresAtUtc);

internal sealed class SshLeaseStore(OperationHost host)
{
    private const string DirectoryPath = "/var/lib/aiursoft-dms-ssh";

    public async Task<FileStream> LockAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = new DirectoryInfo(host.FilePath(DirectoryPath));
        if (directory.LinkTarget != null) throw new IOException("The SSH data directory is a symbolic link.");
        const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory.FullName, privateMode);
        if (File.GetUnixFileMode(directory.FullName) != privateMode)
            throw new IOException("The SSH data directory must be private.");

        var path = Path.Combine(directory.FullName, "lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when (exception.HResult == 11)
            {
                await Task.Delay(100, token);
            }
        }
    }

    public SshLeaseState? ReadState() => Read<SshLeaseState>("state.json");
    public void SaveState(SshLeaseState state) => Save("state.json", state);
    public SshRevocation? ReadRevocation(Guid workflowId) => Read<SshRevocation>($"revoke-{workflowId:N}.json");
    public void SaveRevocation(Guid workflowId, SshRevocation revocation) => Save($"revoke-{workflowId:N}.json", revocation);
    public SshOpening? ReadOpening(Guid workflowId) => Read<SshOpening>($"open-{workflowId:N}.json");
    public void SaveOpening(Guid workflowId, SshOpening opening) => Save($"open-{workflowId:N}.json", opening);

    public static async Task<bool> ShouldRecoverAsync(OperationHost host, SshLeaseState state, SshLease lease, CancellationToken token) =>
        !state.Healthy || lease.ExpiresAtUtc <= DateTimeOffset.UtcNow ||
        (lease.BootId != null && (lease.ExpiresAtUtc == null || lease.BootId != await BootIdAsync(host, token)));

    public static async Task<string> BootIdAsync(OperationHost host, CancellationToken token)
    {
        var value = (await host.ReadAsync("/proc/sys/kernel/random/boot_id", token, 256)).Trim();
        return Guid.TryParse(value, out var id) ? id.ToString("D") : throw new IOException("Cannot read the machine boot identity.");
    }

    private T? Read<T>(string name) where T : class
    {
        try
        {
            using var file = File.OpenRead(host.FilePath(Path.Combine(DirectoryPath, name)));
            return JsonSerializer.Deserialize<T>(file) ?? throw new InvalidDataException($"Invalid SSH state file {name}.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void Save<T>(string name, T value)
    {
        var path = host.FilePath(Path.Combine(DirectoryPath, name));
        var temporary = path + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
