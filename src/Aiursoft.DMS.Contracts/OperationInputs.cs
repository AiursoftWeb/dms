using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Contracts;

public interface IOperationInput
{
    void Validate();
}

public interface IArtifactOperationInput
{
    ArtifactReference Artifact { get; }
}

public interface IAuditTextInput : IArtifactOperationInput
{
}

public static partial class OperationValidation
{
    public static void Path(string path, bool allowRoot = true)
    {
        DirectoryUsageInput.ValidatePath(path);
        if ((!allowRoot && path == "/") || (path != "/" && path.EndsWith('/')))
            throw new ArgumentException("Use one normalized absolute path without a trailing slash.");
    }

    public static void Name(string? name)
    {
        if (name == null || !ResourceName().IsMatch(name)) throw new ArgumentException("Invalid resource name.");
    }

    public static void Identifier(string? name)
    {
        if (name == null || !SqlIdentifier().IsMatch(name))
            throw new ArgumentException("Use a simple database identifier.");
    }

    public static void Limit(int count, int maximum = 1000)
    {
        if (count < 1 || count > maximum) throw new ArgumentException($"Limit must be between 1 and {maximum}.");
    }

    public static void Window(DateTimeOffset? since, DateTimeOffset? until)
    {
        if (since != null && until != null && (until <= since || until - since > TimeSpan.FromDays(7)))
            throw new ArgumentException("Use an increasing time window of at most seven days.");
    }

    [GeneratedRegex(@"\A[a-zA-Z0-9][a-zA-Z0-9_.@-]{0,127}\z")]
    private static partial Regex ResourceName();

    [GeneratedRegex(@"\A[a-zA-Z_][a-zA-Z0-9_]{0,127}\z")]
    private static partial Regex SqlIdentifier();
}

public sealed record PathInput(string Path) : IOperationInput
{
    public void Validate() => OperationValidation.Path(Path);
}

public sealed record ListPathInput(string Path, int Limit = 200, int Depth = 1) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Path);
        OperationValidation.Limit(Limit);
        if (Depth is < 1 or > 5) throw new ArgumentException("Depth must be 1 to 5.");
    }
}

public sealed record TextFileInput(string Path, int Lines = 100, int MaxBytes = 65536) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Path, false);
        OperationValidation.Limit(Lines, 10000);
        OperationValidation.Limit(MaxBytes, 1048576);
    }
}

public sealed record FindFilesInput(
    string Path,
    string? Name = null,
    long? MinBytes = null,
    DateTimeOffset? ModifiedAfter = null,
    int Limit = 200,
    int Depth = 5) : IOperationInput
{
    public void Validate()
    {
        new ListPathInput(Path, Limit, Depth).Validate();
        if (MinBytes < 0 || Name is { Length: > 255 } || Name?.Any(char.IsControl) == true ||
            Name?.Contains('/') == true)
            throw new ArgumentException("Invalid file name or minimum size.");
    }
}

public sealed record MakeDirectoryInput(string Path, bool Parents = false, int Mode = 448) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Path, false);
        if (Mode is < 0 or > 511)
            throw new ArgumentException("Mode must contain only the nine rwx permission bits (default 448 = 0700).");
    }
}

public sealed record CopyPathInput(string Source, string Destination, bool Recursive = false, bool Overwrite = false)
    : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Source, false);
        OperationValidation.Path(Destination, false);
        if (Source == Destination || Destination.StartsWith(Source + "/", StringComparison.Ordinal))
            throw new ArgumentException("Source and destination must be separate paths.");
    }
}

public sealed record ExportPathInput(string Path, bool Recursive = false, long MaxBytes = 268435456) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Path, false);
        if (MaxBytes is < 1 or > 1073741824) throw new ArgumentException("Export limit must be 1 byte to 1 GiB.");
    }
}

public sealed record ArtifactReference(
    Guid Id,
    string Sha256,
    long Length,
    string Kind = "file",
    string Name = "artifact")
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Sha256);
        if (Id == Guid.Empty || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit)
            || Length is < 0 or > 1073741824 || Kind is not ("file" or "directory-tar")
            || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Name.Any(char.IsControl) || Name.Contains('/') ||
            Name.Contains('\\'))
            throw new ArgumentException("Invalid immutable artifact reference.");
    }
}

public sealed record ImportPathInput(ArtifactReference Artifact, string Path, bool Overwrite = false)
    : IOperationInput, IArtifactOperationInput
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Artifact);
        Artifact.Validate();
        OperationValidation.Path(Path, false);
    }
}

public sealed record DeviceInput(string Device) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Path(Device, false);
        if (!Device.StartsWith("/dev/", StringComparison.Ordinal))
            throw new ArgumentException("Use a device path under /dev.");
    }
}

public sealed record SmartTestInput(string Device, int WaitSeconds = 120) : IOperationInput
{
    public void Validate()
    {
        new DeviceInput(Device).Validate();
        if (WaitSeconds is < 0 or > 900) throw new ArgumentException("Wait must be 0 to 900 seconds.");
    }
}

public sealed record BaselineInput(string? BaselinePath = null) : IOperationInput
{
    public void Validate()
    {
        if (BaselinePath != null) OperationValidation.Path(BaselinePath, false);
    }
}

public sealed record SampleInput(int Seconds = 10, int Interval = 1, int Limit = 50) : IOperationInput
{
    public void Validate()
    {
        if (Seconds is < 1 or > 300 || Interval < 1 || Interval > Seconds || Seconds / Interval > 300)
            throw new ArgumentException("Sample 1 to 300 seconds with an interval of at least one second.");
        OperationValidation.Limit(Limit);
    }
}

public sealed record ProcessListInput(int Limit = 50, string Sort = "cpu") : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Limit(Limit);
        if (Sort is not ("cpu" or "memory" or "pid")) throw new ArgumentException("Sort must be cpu, memory or pid.");
    }
}

public sealed record EventWindowInput(DateTimeOffset? Since = null, DateTimeOffset? Until = null, int Limit = 200)
    : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Window(Since, Until);
        OperationValidation.Limit(Limit, 10000);
    }
}

public sealed record ContainerActionInput(string Container, int TimeoutSeconds = 30) : IOperationInput
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        if (TimeoutSeconds is < 1 or > 120) throw new ArgumentException("Stop timeout must be 1 to 120 seconds.");
    }
}

public sealed record ContainerEventsInput(
    string Container,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    int Limit = 200) : IOperationInput
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        new EventWindowInput(Since, Until, Limit).Validate();
    }
}

public sealed record ContainerExportInput(
    string Container,
    string Path,
    bool Recursive = false,
    long MaxBytes = 268435456) : IOperationInput
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        new ExportPathInput(Path, Recursive, MaxBytes).Validate();
    }
}

public sealed record ContainerCopyInput(
    string Container,
    string Source,
    string Destination,
    bool Recursive = false,
    bool Overwrite = false) : IOperationInput
{
    public void Validate()
    {
        new DockerContainerInput(Container).Validate();
        OperationValidation.Path(Source, false);
        OperationValidation.Path(Destination, false);
    }
}

public sealed record SwarmServiceInput(string Service) : IOperationInput
{
    public void Validate() => OperationValidation.Name(Service);
}

public sealed record SwarmLogsInput(string Service, int Lines = 100, DateTimeOffset? Since = null) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Name(Service);
        OperationValidation.Limit(Lines);
    }
}

public sealed record SwarmChangeInput(
    string Service,
    long ExpectedVersion,
    string[] NodeIds,
    string? Image = null,
    int Replicas = 1,
    int TimeoutSeconds = 120) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Name(Service);
        ArgumentNullException.ThrowIfNull(NodeIds);
        if (ExpectedVersion < 1 || NodeIds.Length is < 1 or > 1000 ||
            NodeIds.Distinct(StringComparer.Ordinal).Count() != NodeIds.Length)
            throw new ArgumentException("Freeze a service version and explicit Swarm node IDs in the approval.");
        foreach (var node in NodeIds) OperationValidation.Name(node);
        if (Replicas is < 0 or > 1000 || TimeoutSeconds is < 1 or > 600 || Image is { Length: > 256 }
            || Image != null && (Image.StartsWith('-') || Image.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            throw new ArgumentException("Invalid image, replica count or convergence timeout.");
    }

    public void ValidateFor(string action)
    {
        Validate();
        if (action == "update" && string.IsNullOrWhiteSpace(Image))
            throw new ArgumentException("Service update requires an explicit target image.");
        if (action != "update" && Image != null) throw new ArgumentException("Only service update accepts an image.");
        if (action == "start" && Replicas == 0)
            throw new ArgumentException("Starting a service requires at least one replica.");
        if (action != "start" && Replicas != 1)
            throw new ArgumentException("Only service start accepts a replica count.");
    }
}

public sealed record ServiceListInput(bool FailedOnly = false, int Limit = 200) : IOperationInput
{
    public void Validate() => OperationValidation.Limit(Limit);
}

public sealed record JournalInput(
    string? Service = null,
    string? Boot = null,
    string? Level = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null,
    int Limit = 200,
    bool Kernel = false,
    string? BeforeCursor = null) : IOperationInput
{
    public void Validate()
    {
        var end = Until ?? DateTimeOffset.UtcNow;
        OperationValidation.Window(Since ?? end.AddHours(-24), end);
        if (Kernel && Service != null) throw new ArgumentException("Kernel journal cannot select a service.");
        if (BeforeCursor != null && (BeforeCursor.Length > 1024 || BeforeCursor.Any(char.IsControl) ||
                                     BeforeCursor.StartsWith('-')))
            throw new ArgumentException("Invalid journal cursor.");
        if (Service != null) new ServiceInput(Service).Validate();
        if (Boot != null && Boot != "0" && Boot != "-1" && (Boot.Length != 32 || !Boot.All(char.IsAsciiHexDigit)))
            throw new ArgumentException("Boot must be 0, -1 or a boot ID.");
        if (Level != null &&
            Level is not ("emerg" or "alert" or "crit" or "err" or "warning" or "notice" or "info" or "debug"))
            throw new ArgumentException("Invalid journal priority.");
        new EventWindowInput(Since, Until, Limit).Validate();
    }
}

public sealed record PackageInput(string[] Packages) : IOperationInput
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Packages);
        if (Packages.Length is < 1 or > 100 || Packages.Any(p =>
                string.IsNullOrEmpty(p) || p.Length > 200 || !Regex.IsMatch(p,
                    @"\A[a-z0-9][a-z0-9+.-]*(?::[a-z0-9-]+)?(?:=[a-zA-Z0-9.+:~_-]+)?\z",
                    RegexOptions.CultureInvariant)))
            throw new ArgumentException("Specify 1 to 100 exact package names, optionally with =version.");
    }
}

public sealed record PackageUpgradeInput(string[] Packages) : IOperationInput
{
    public void Validate()
    {
        new PackageInput(Packages).Validate();
        if (Packages.Any(p => !p.Contains('=')))
            throw new ArgumentException("Freeze each target package as name=version in the approval.");
    }
}

public sealed record RebootInput(int DelaySeconds = 15) : IOperationInput
{
    public void Validate()
    {
        if (DelaySeconds is < 5 or > 120) throw new ArgumentException("Reboot delay must be 5 to 120 seconds.");
    }
}

public sealed record ScriptInput(string Language, ArtifactReference Artifact, int TimeoutSeconds = 60)
    : IOperationInput, IAuditTextInput
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Artifact);
        Artifact.Validate();
        if (Language is not ("bash" or "python") || Artifact.Kind != "file" || Artifact.Length is < 1 or > 262144 ||
            TimeoutSeconds is < 1 or > 3600)
            throw new ArgumentException(
                "Use bash or python, a frozen script artifact of at most 256 KiB and a timeout of 1 to 3600 seconds.");
    }
}

public sealed record DatabaseInput(string Connection) : IOperationInput
{
    public void Validate() => OperationValidation.Name(Connection);
}

public sealed record DatabaseNameInput(string Connection, string Database) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Name(Connection);
        OperationValidation.Identifier(Database);
    }
}

public sealed record DatabaseTableInput(string Connection, string Database, string Table) : IOperationInput
{
    public void Validate()
    {
        new DatabaseNameInput(Connection, Database).Validate();
        OperationValidation.Identifier(Table);
    }
}

public sealed record DatabaseFilter(string Column, string Operator, JsonElement Value)
{
    public void Validate()
    {
        OperationValidation.Identifier(Column);
        if (Operator is not ("eq" or "ne" or "lt" or "le" or "gt" or "ge" or "contains" or "is-null"))
            throw new ArgumentException("Unsupported typed filter operator.");
        if (Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
                or JsonValueKind.True or JsonValueKind.False)
            || Value.GetRawText().Length > 4096)
            throw new ArgumentException("Filter values must be bounded scalar JSON values.");
    }
}

public sealed record DatabaseQueryInput(
    string Connection,
    string Database,
    string Table,
    string[] Columns,
    DatabaseFilter[]? Filters = null,
    string? SortBy = null,
    bool Descending = false,
    int Limit = 100,
    int Offset = 0) : IOperationInput
{
    public void Validate()
    {
        new DatabaseTableInput(Connection, Database, Table).Validate();
        ArgumentNullException.ThrowIfNull(Columns);
        if (Columns.Length is < 1 or > 100 || Filters is { Length: > 20 })
            throw new ArgumentException("Select 1 to 100 explicit columns and at most 20 filters.");
        foreach (var column in Columns) OperationValidation.Identifier(column);
        foreach (var filter in Filters ?? [])
        {
            ArgumentNullException.ThrowIfNull(filter);
            filter.Validate();
        }

        if (SortBy != null) OperationValidation.Identifier(SortBy);
        OperationValidation.Limit(Limit);
        if (Offset is < 0 or > 1000000) throw new ArgumentException("Offset must be 0 to 1000000.");
    }
}

public sealed record DatabaseSqlInput(
    string Connection,
    ArtifactReference Artifact,
    int TimeoutSeconds = 30,
    int MaxRows = 1000) : IOperationInput, IAuditTextInput
{
    public void Validate()
    {
        OperationValidation.Name(Connection);
        OperationValidation.Limit(MaxRows, 10000);
        ArgumentNullException.ThrowIfNull(Artifact);
        Artifact.Validate();
        if (Artifact.Kind != "file" || Artifact.Length is < 1 or > 131072 || TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException(
                "Supply a frozen SQL artifact of at most 128 KiB and a timeout of 1 to 300 seconds.");
    }
}

public sealed record DnsQueryInput(string Host) : IOperationInput
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown ||
            Host.StartsWith('-')) throw new ArgumentException("Use an explicit DNS name or IP address.");
    }
}

public sealed record NetworkProbeInput(string Host, int Port = 443, string? Url = null, int TimeoutSeconds = 10)
    : IOperationInput
{
    public void Validate()
    {
        new DnsQueryInput(Host).Validate();
        if (Port is < 1 or > 65535 || TimeoutSeconds is < 1 or > 30)
            throw new ArgumentException("Invalid probe port or timeout.");
        if (Url != null && (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
                            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Host != Host ||
                            uri.Port != Port || uri.Fragment.Length > 0))
            throw new ArgumentException(
                "URL must be HTTP(S) on the approved host and port, without credentials or fragment.");
    }
}

public sealed record SshAuditInput(string User = "root", string Address = "127.0.0.1", string Host = "localhost")
    : IOperationInput
{
    public void Validate()
    {
        OperationValidation.Name(User);
        new DnsQueryInput(Address).Validate();
        new DnsQueryInput(Host).Validate();
    }
}

public sealed record OperationResult(
    string Summary,
    bool Complete = true,
    ArtifactReference? Artifact = null,
    JsonElement? Facts = null);
