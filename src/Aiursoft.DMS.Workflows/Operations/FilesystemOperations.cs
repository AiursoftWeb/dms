using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class SafeOperationPaths
{
    public static void Check(string path, bool leafMayBeLink = false, bool missingParents = false)
    {
        OperationValidation.Path(path);
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 && !(leafMayBeLink && current == path))
                    throw new IOException("Symbolic links are not permitted in this operation's path.");
            }
            catch (FileNotFoundException) when (current == path || missingParents) { }
            catch (DirectoryNotFoundException) when (missingParents) { }
        }
    }
    public static object Metadata(string path)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return new
        {
            Path = path, Directory = (attributes & FileAttributes.Directory) != 0,
            SymbolicLink = (attributes & FileAttributes.ReparsePoint) != 0, info.LinkTarget,
            Bytes = info is FileInfo file && info.LinkTarget == null ? file.Length : (long?)null,
            info.LastWriteTimeUtc, info.CreationTimeUtc,
            Mode = OperatingSystem.IsLinux() && info.LinkTarget == null ? Convert.ToString((int)File.GetUnixFileMode(path), 8) : null
        };
    }
    public static string TemporaryDirectory()
    {
        var path = Directory.CreateTempSubdirectory("dms-operation-").FullName;
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}

public static class FilesystemOperations
{
    private static HashSet<string> MountPoints() => File.ReadAllLines("/proc/self/mountinfo")
        .Select(line => line.Split(' ')[4].Replace("\\040", " ", StringComparison.Ordinal)
            .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);
    public static async Task<OperationResult> LsAsync(MachineWorkflowContext context, ListPathInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Path);
        List<object> rows = [];
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((input.Path, 0));
        var complete = true;
        while (queue.TryDequeue(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (rows.Count == input.Limit) { complete = false; break; }
                rows.Add(SafeOperationPaths.Metadata(entry));
                var attributes = File.GetAttributes(entry);
                if (directory.Depth + 1 < input.Depth && (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
                    queue.Enqueue((entry, directory.Depth + 1));
            }
            if (!complete) break;
        }
        return await OperationOutput.ReportAsync(context, $"Listed {rows.Count} entries; complete={complete}.", rows, complete);
    }
    private const string BoxSecretPathPattern = "/swarm-vol/boxfor*/.env.prod";
    private const string BoxSecretStagePattern = "/swarm-vol/boxfor*/.git/.dms-env-prod-*";

    private static void RequirePublicContentPath(string path)
    {
        if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(BoxSecretPathPattern, path, ignoreCase: true) ||
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(BoxSecretStagePattern, path, ignoreCase: true))
            throw new IOException("Use the Box secret workflows to access .env.prod.");
    }

    public static async Task<OperationResult> StatAsync(MachineWorkflowContext context, PathInput input, AgentEnvironment agent, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); input.Validate(); SafeOperationPaths.Check(input.Path, leafMayBeLink: true);
        var ownership = await OperationOutput.CommandAsync(context, agent, "stat", ["--printf=%u\t%U\t%g\t%G\t%i\t%h\t%s\t%a\t%F\n", "--", input.Path], token, traceOutput: false);
        return await OperationOutput.ReportAsync(context, "File metadata and ownership collected without dereferencing a leaf symlink.", new { Metadata = SafeOperationPaths.Metadata(input.Path), OwnershipAndInode = ownership.Output, Fields = "uid,user,gid,group,inode,links,size,mode,type" });
    }
    private const int PublicReadDenied = 77;
    private const string StartPublicReader = """
        if (( EUID == 0 )); then
            exec /usr/bin/setpriv --reuid=65534 --regid=65534 --clear-groups \
                --inh-caps=-all --ambient-caps=-all --bounding-set=-all --no-new-privs \
                /bin/bash -c "$1" dms-cat "$2" "$3"
        fi
        exec /bin/bash -c "$1" dms-cat "$2" "$3"
        """;

    // Check and read the same open file. The pathname can change between operations.
    private const string ReadPublicFile = """
        path=$1
        [[ -f "$path" && ! -L "$path" ]] || exit 77
        { exec {file}<"$path"; } 2>/dev/null || exit 77
        fd=/proc/self/fd/$file
        [[ -f "$fd" ]] || exit 77
        metadata=$(/usr/bin/stat -Lc '%a %s' -- "$fd") || exit 77
        read -r mode size <<< "$metadata"
        (( (8#$mode & 4) != 0 )) || exit 77
        printf '%s\n' "$size"
        exec /usr/bin/head --bytes="$2" <&"$file"
        """;

    public static Task<OperationResult> CatAsync(MachineWorkflowContext context, TextFileInput input, AgentEnvironment agent, CancellationToken token) => TextAsync(context, input, agent, tail: false, requireOtherRead: true, token);
    public static Task<OperationResult> CatAsRootAsync(MachineWorkflowContext context, TextFileInput input, AgentEnvironment agent, CancellationToken token) => TextAsync(context, input, agent, tail: false, requireOtherRead: false, token);
    public static Task<OperationResult> TailAsync(MachineWorkflowContext context, TextFileInput input, AgentEnvironment agent, CancellationToken token) => TextAsync(context, input, agent, tail: true, requireOtherRead: false, token);

    private static async Task<OperationResult> TextAsync(MachineWorkflowContext context, TextFileInput input, AgentEnvironment agent, bool tail, bool requireOtherRead, CancellationToken token)
    {
        input.Validate(); RequirePublicContentPath(input.Path); SafeOperationPaths.Check(input.Path);
        if (Directory.Exists(input.Path)) throw new IOException("Reading a directory requires export with explicit recursion.");
        var maxBytes = input.MaxBytes.ToString(CultureInfo.InvariantCulture);
        string output;
        long length;
        if (requireOtherRead)
        {
            var result = await OperationOutput.CommandAsync(context, agent, "bash",
                ["-c", StartPublicReader, "dms-cat", ReadPublicFile, input.Path, maxBytes], token,
                traceOutput: false, allowedExitCodes: [0, PublicReadDenied]);
            if (result.ExitCode == PublicReadDenied)
                throw new IOException("Cannot read this file as an unprivileged user. For a restricted file, use 'dms machine filesystem cat-as-root'.");
            var separator = result.Output.IndexOf('\n');
            if (separator < 0 || !long.TryParse(result.Output.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out length))
                throw new IOException("The file reader did not report its size.");
            output = result.Output[(separator + 1)..];
        }
        else
        {
            // Tail's final --bytes option bounds bytes; line limiting is applied after that bounded read.
            var arguments = tail ? new[] { "--lines=" + input.Lines, "--bytes=" + maxBytes, "--", input.Path }
                : new[] { "--bytes=" + maxBytes, "--", input.Path };
            var result = await OperationOutput.CommandAsync(context, agent, tail ? "tail" : "head", arguments, token, traceOutput: false);
            output = result.Output;
            length = new FileInfo(input.Path).Length;
        }
        if (output.Contains('\0')) throw new IOException("The file contains binary data. Export it as an artifact.");
        var endedWithNewline = output.EndsWith('\n');
        var lines = output.Split('\n').AsEnumerable(); if (endedWithNewline) lines = lines.SkipLast(1);
        var allLines = lines.ToArray();
        var text = string.Join('\n', tail ? allLines.TakeLast(input.Lines) : allLines.Take(input.Lines));
        if (endedWithNewline && text.Length > 0) text += "\n";
        await OperationOutput.TraceTextAsync(context, text);
        var complete = length <= input.MaxBytes && (tail || allLines.Length <= input.Lines);
        return await OperationOutput.ReportAsync(context, "Bounded text read completed.", new { input.Path, BytesRead = System.Text.Encoding.UTF8.GetByteCount(text), Bounded = !complete }, complete);
    }
    public static async Task<OperationResult> FindAsync(MachineWorkflowContext context, FindFilesInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Path);
        List<object> rows = [];
        var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((input.Path, 0));
        var inspected = 0; var complete = true;
        while (queue.TryDequeue(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++inspected > 100000 || rows.Count == input.Limit) { complete = false; break; }
                var attributes = File.GetAttributes(entry);
                var linked = (attributes & FileAttributes.ReparsePoint) != 0;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && !linked && directory.Depth + 1 < input.Depth) queue.Enqueue((entry, directory.Depth + 1));
                if (input.Name != null && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(input.Name, Path.GetFileName(entry), false)) continue;
                if (input.ModifiedAfter != null && File.GetLastWriteTimeUtc(entry) < input.ModifiedAfter.Value.UtcDateTime) continue;
                if (input.MinBytes != null && (linked || isDirectory || new FileInfo(entry).Length < input.MinBytes)) continue;
                rows.Add(SafeOperationPaths.Metadata(entry));
            }
            if (!complete) break;
        }
        return await OperationOutput.ReportAsync(context, $"Found {rows.Count} matching entries after inspecting {inspected}.", rows, complete);
    }
    public static async Task<OperationResult> MkdirAsync(MachineWorkflowContext context, MakeDirectoryInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Path, missingParents: input.Parents);
        if (Directory.Exists(input.Path) || File.Exists(input.Path)) throw new IOException("The target already exists.");
        if (!input.Parents && !Directory.Exists(Path.GetDirectoryName(input.Path))) throw new DirectoryNotFoundException("The parent directory does not exist.");
        List<string> args = ["--mode=" + Convert.ToString(input.Mode, 8)]; if (input.Parents) args.Add("--parents"); args.AddRange(["--", input.Path]);
        await OperationOutput.CommandAsync(context, agent, "mkdir", args, token, change: true);
        return await OperationOutput.ReportAsync(context, "Directory created.", SafeOperationPaths.Metadata(input.Path));
    }
    public static Task<OperationResult> CpAsync(MachineWorkflowContext context, CopyPathInput input, AgentEnvironment agent, CancellationToken token) => CopyAsync(context, input, agent, false, token);
    public static Task<OperationResult> MvAsync(MachineWorkflowContext context, CopyPathInput input, AgentEnvironment agent, CancellationToken token) => CopyAsync(context, input, agent, true, token);
    private static async Task<OperationResult> CopyAsync(MachineWorkflowContext context, CopyPathInput input, AgentEnvironment agent, bool move, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Source); SafeOperationPaths.Check(input.Destination);
        RequirePublicContentPath(input.Destination);
        var directory = Directory.Exists(input.Source);
        if (directory && !input.Recursive) throw new IOException("Directory operations require explicit recursive approval.");
        if (!input.Overwrite && (Directory.Exists(input.Destination) || File.Exists(input.Destination))) throw new IOException("The destination exists; overwrite was not approved.");
        await ValidateTransferTreeAsync(context, input.Source, agent, token);
        List<string> arguments = ["--no-target-directory"];
        if (move) arguments.Add(input.Overwrite ? "--force" : "--no-clobber");
        else { arguments.AddRange(["--no-dereference", "--one-file-system", input.Overwrite ? "--force" : "--no-clobber"]); if (directory) arguments.Add("--recursive"); }
        arguments.AddRange(["--", input.Source, input.Destination]);
        await OperationOutput.CommandAsync(context, agent, move ? "mv" : "cp", arguments, token, change: true, seconds: 600);
        if (move && (File.Exists(input.Source) || Directory.Exists(input.Source))) throw new WorkflowNeedsVerificationException(context.Id);
        return await OperationOutput.ReportAsync(context, move ? "Path moved." : "Path copied.", new { input.Source, input.Destination, input.Recursive, input.Overwrite });
    }

    public static async Task ValidateTransferTreeAsync(MachineWorkflowContext context, string path, AgentEnvironment agent, CancellationToken token)
    {
        SafeOperationPaths.Check(path);
        RequirePublicContentPath(path);
        if (Directory.Exists(path) && MountPoints().Any(mount => mount != path && mount.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw new IOException("Directory transfers refuse nested mounts; approve each mounted source separately.");
        var result = await OperationOutput.CommandAsync(context, agent, "find", [path, "-xdev", "(", "-type", "l", "-o", "-type", "b", "-o", "-type", "c", "-o", "-type", "p", "-o", "-type", "s", "-o", "-ipath", BoxSecretPathPattern, "-o", "-ipath", BoxSecretStagePattern, ")", "-print", "-quit"], token, traceOutput: false);
        if (!string.IsNullOrWhiteSpace(result.Output)) throw new IOException("File transfer requires regular files or directories and excludes Box secrets, links and special files.");
    }

    public static async Task<OperationResult> ExportAsync(MachineWorkflowContext context, ExportPathInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var artifacts = agent.Artifacts ?? throw new NotSupportedException("This Agent host has no artifact transport.");
        await ValidateTransferTreeAsync(context, input.Path, agent, token);
        var directory = Directory.Exists(input.Path);
        if (directory && !input.Recursive) throw new IOException("Directory export requires explicit recursive approval.");
        var temporary = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var file = Path.Combine(temporary, "payload");
            var kind = directory ? "directory-tar" : "file";
            var entries = directory ? await PackDirectoryAsync(input.Path, file, input.MaxBytes, token) : 1;
            if (!directory)
            {
                if (new FileInfo(input.Path).Length > input.MaxBytes) throw new IOException("File exceeds the approved export limit.");
                await CopyBoundedAsync(input.Path, file, input.MaxBytes, token);
            }
            if (new FileInfo(file).Length > input.MaxBytes) throw new IOException("The completed archive exceeds the approved byte limit.");
            var artifact = await artifacts.UploadAsync(context.Id, file, Path.GetFileName(input.Path) + (directory ? ".tar" : ""), kind, token);
            return await OperationOutput.ReportAsync(context, "Export artifact is ready for an authorized download.", new { Artifact = artifact, Entries = entries, Consistency = "Files captured during this operation; not a filesystem snapshot." }, artifact: artifact);
        }
        finally { Directory.Delete(temporary, true); }
    }
    public static async Task<OperationResult> ImportAsync(MachineWorkflowContext context, ImportPathInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Path);
        var artifacts = agent.Artifacts ?? throw new NotSupportedException("This Agent host has no artifact transport.");
        var temporary = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var payload = Path.Combine(temporary, "payload");
            await artifacts.DownloadAsync(input.Artifact, payload, token);
            await VerifyArtifactAsync(input.Artifact, payload, token);
            if (input.Artifact.Kind == "directory-tar")
            {
                var tree = Path.Combine(temporary, "tree"); Directory.CreateDirectory(tree);
                await ExtractDirectoryAsync(payload, tree, 1073741824, token);
                return await CopyAsync(context, new CopyPathInput(tree, input.Path, true, input.Overwrite), agent, false, token);
            }
            return await CopyAsync(context, new CopyPathInput(payload, input.Path, false, input.Overwrite), agent, false, token);
        }
        finally { Directory.Delete(temporary, true); }
    }

    public static async Task VerifyArtifactAsync(ArtifactReference artifact, string file, CancellationToken token)
    {
        artifact.Validate();
        await using var stream = File.OpenRead(file);
        if (stream.Length != artifact.Length || !Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Artifact bytes differ from the approved length or SHA256.");
    }
    public static async Task<string> ReadArtifactTextAsync(ArtifactReference artifact, AgentEnvironment agent, int maximum, CancellationToken token)
    {
        artifact.Validate();
        if (artifact.Kind != "file" || artifact.Length > maximum) throw new ArgumentException("Text artifact exceeds its operation limit.");
        var transport = agent.Artifacts ?? throw new NotSupportedException("The Agent host has no artifact transport.");
        var directory = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "text"); await transport.DownloadAsync(artifact, path, token);
            await VerifyArtifactAsync(artifact, path, token);
            var text = await File.ReadAllTextAsync(path, new System.Text.UTF8Encoding(false, true), token);
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || text.Length > maximum) throw new InvalidDataException("Text artifact must contain bounded UTF-8 text without NUL.");
            return text;
        }
        finally { Directory.Delete(directory, true); }
    }
    public static async Task CopyBoundedAsync(string source, string destination, long maximum, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            total = checked(total + count); if (total > maximum) throw new IOException("File exceeds its transfer limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        await output.FlushAsync(token);
    }
    public static async Task<int> PackDirectoryAsync(string source, string archive, long maximum, CancellationToken token)
    {
        var queue = new Queue<string>(); queue.Enqueue(source);
        long total = 0; var entries = 0;
        await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new TarWriter(output, leaveOpen: true);
        var mounts = MountPoints();
        while (queue.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 10000 || mounts.Contains(path)) throw new IOException("Export refuses nested mounts or more than 10000 entries.");
                RequirePublicContentPath(path);
                SafeOperationPaths.Check(path);
                var relative = Path.GetRelativePath(source, path);
                if (Directory.Exists(path))
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute }, token);
                    queue.Enqueue(path);
                }
                else
                {
                    total = checked(total + new FileInfo(path).Length); if (total > maximum) throw new IOException("Directory exceeds the approved export limit.");
                    await writer.WriteEntryAsync(path, relative, token);
                }
                if (output.Length > maximum) throw new IOException("The archive exceeds the approved export limit.");
            }
        }
        return entries;
    }
    public static async Task ExtractDirectoryAsync(string archive, string destination, long maximum, CancellationToken token)
    {
        await using var file = File.OpenRead(archive);
        await using var reader = new TarReader(file);
        long total = 0; var count = 0;
        TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(cancellationToken: token)) != null)
        {
            if (++count > 10000 || entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile)) throw new InvalidDataException("Archive contains a link/special entry or too many entries.");
            var relative = entry.Name.TrimEnd('/');
            if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or "." or "") || relative.Any(char.IsControl))
                throw new InvalidDataException("Archive path is not a relative canonical path.");
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Archive path escapes its destination.");
            total = checked(total + entry.Length); if (total > maximum) throw new InvalidDataException("Archive exceeds the extraction limit.");
            if (entry.EntryType == TarEntryType.Directory) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await entry.ExtractToFileAsync(target, overwrite: false, cancellationToken: token);
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }
}
