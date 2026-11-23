using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class DockerOperations
{
    internal static HttpClient ArchiveClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false,
        ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), token); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); throw; }
        }
    }) { BaseAddress = new Uri("http://docker/"), Timeout = TimeSpan.FromMinutes(10) };

    private static string ArchiveRoute(string container, string path) => "containers/" + container + "/archive?path=" + Uri.EscapeDataString(path);
    private sealed record ContainerPathStat(string Name, long Size, uint Mode, string? LinkTarget);
    private static async Task<ContainerPathStat?> ContainerStatAsync(HttpClient http, string container, string path, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, ArchiveRoute(container, path));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (!response.Headers.TryGetValues("X-Docker-Container-Path-Stat", out var values)) throw new InvalidDataException("Docker did not provide path metadata.");
        return JsonSerializer.Deserialize<ContainerPathStat>(Convert.FromBase64String(values.Single()), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Invalid Docker path metadata.");
    }
    private static async Task<ContainerPathStat?> CheckContainerPathAsync(HttpClient http, string container, string path, bool allowMissingLeaf, CancellationToken token)
    {
        ContainerPathStat? leaf = null;
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            var stat = await ContainerStatAsync(http, container, current, token);
            if (stat == null)
            {
                if (current == path && allowMissingLeaf) continue;
                throw new FileNotFoundException("A container path component does not exist.");
            }
            if (!string.IsNullOrEmpty(stat.LinkTarget) || (stat.Mode & (1u << 27)) != 0) throw new IOException("Container transfer through a symbolic link is refused.");
            if (current != path && (stat.Mode & (1u << 31)) == 0) throw new IOException("A container path parent is not a directory.");
            if (current == path) leaf = stat;
        }
        return leaf;
    }
    private static async Task<string> ReceiveContainerPathAsync(HttpClient http, string container, string path, bool recursive,
        long maximum, string temporary, CancellationToken token)
    {
        var stat = await CheckContainerPathAsync(http, container, path, false, token) ?? throw new FileNotFoundException();
        if ((stat.Mode & (1u << 31)) != 0 && !recursive) throw new IOException("Container directories require explicit recursive approval.");
        var archive = Path.Combine(temporary, "container.tar");
        using (var response = await http.GetAsync(ArchiveRoute(container, path), HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[65536]; long length = 0; int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                length += count; if (length > maximum) throw new IOException("Container archive exceeds the approved byte limit.");
                await output.WriteAsync(buffer.AsMemory(0, count), token);
            }
        }
        var extracted = Path.Combine(temporary, "extracted"); Directory.CreateDirectory(extracted);
        await FilesystemOperations.ExtractDirectoryAsync(archive, extracted, maximum, token);
        var entries = Directory.GetFileSystemEntries(extracted);
        if (entries.Length != 1) throw new InvalidDataException("Container archive did not contain exactly the requested path.");
        return entries[0];
    }
    private static void CheckContainerMountSources(JsonElement containerInspect, string containerPath)
    {
        if (!containerInspect.TryGetProperty("Mounts", out var mounts) || mounts.ValueKind != JsonValueKind.Array)
            return;
        foreach (var mount in mounts.EnumerateArray())
        {
            var dest = mount.TryGetProperty("Destination", out var d) ? d.GetString() : null;
            var src = mount.TryGetProperty("Source", out var s) ? s.GetString() : null;
            if (string.IsNullOrEmpty(dest) || string.IsNullOrEmpty(src)) continue;

            if (containerPath.Equals(dest, StringComparison.Ordinal) ||
                containerPath.StartsWith(dest.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                var relative = Path.GetRelativePath(dest, containerPath);
                var hostPath = Path.GetFullPath(Path.Combine(src, relative));
                FilesystemOperations.RequirePublicContentPath(hostPath);
                if (OperatingSystem.IsLinux() && (File.Exists(hostPath) || Directory.Exists(hostPath)))
                {
                    var mode = File.GetUnixFileMode(hostPath);
                    if ((mode & UnixFileMode.OtherRead) == 0)
                        throw new IOException($"Cannot export restricted container mount source '{hostPath}'. For restricted files, use 'dms machine filesystem download-as-root'.");
                }
            }
            else if (dest.StartsWith(containerPath.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                FilesystemOperations.RequirePublicContentPath(src);
                if (OperatingSystem.IsLinux() && (File.Exists(src) || Directory.Exists(src)))
                {
                    var mode = File.GetUnixFileMode(src);
                    if ((mode & UnixFileMode.OtherRead) == 0)
                        throw new IOException($"Cannot export restricted container mount source '{src}'. For restricted files, use 'dms machine filesystem download-as-root'.");
                }
            }
        }
    }

    private static void MakeStagedExportReadable(string directory)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(entry))
                File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            else if (File.Exists(entry))
                File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        }
    }

    public static async Task<OperationResult> FilesystemExportAsync(MachineWorkflowContext context, ContainerExportInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var details = await ReadContainerAsync(context, input.Container, agent, token);
        CheckContainerMountSources(details, input.Path);
        using var http = ArchiveClient(); var temporary = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var source = await ReceiveContainerPathAsync(http, input.Container, input.Path, input.Recursive, input.MaxBytes, temporary, token);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                MakeStagedExportReadable(temporary);
            }
            return await context.CallAsync(OperationsWorkflows.FilesystemExport, new ExportPathInput(source, input.Recursive, input.MaxBytes), token);
        }
        finally { Directory.Delete(temporary, true); }
    }
    public static async Task<OperationResult> FilesystemCpFromAsync(MachineWorkflowContext context, ContainerCopyInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Destination);
        await ReadContainerAsync(context, input.Container, agent, token);
        using var http = ArchiveClient(); var temporary = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var source = await ReceiveContainerPathAsync(http, input.Container, input.Source, input.Recursive, 1073741824, temporary, token);
            return await context.CallAsync(OperationsWorkflows.FilesystemCp, new CopyPathInput(source, input.Destination, input.Recursive, input.Overwrite), token);
        }
        finally { Directory.Delete(temporary, true); }
    }
    public static async Task<OperationResult> FilesystemCpToAsync(MachineWorkflowContext context, ContainerCopyInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); SafeOperationPaths.Check(input.Source);
        await ReadContainerAsync(context, input.Container, agent, token);
        await FilesystemOperations.ValidateTransferTreeAsync(context, input.Source, agent, token);
        if (Directory.Exists(input.Source) && !input.Recursive) throw new IOException("Directory copy requires explicit recursive approval.");
        using var http = ArchiveClient();
        var target = await CheckContainerPathAsync(http, input.Container, input.Destination, true, token);
        if (target != null && !input.Overwrite) throw new IOException("The container destination already exists; overwrite was not approved.");
        var parent = Path.GetDirectoryName(input.Destination)!; var name = Path.GetFileName(input.Destination);
        var temporary = SafeOperationPaths.TemporaryDirectory(); var started = false;
        try
        {
            var archive = Path.Combine(temporary, "upload.tar");
            await BuildContainerArchiveAsync(input.Source, name, archive, temporary, token);
            if (new FileInfo(archive).Length > 1073741824) throw new IOException("Container transfer archive exceeds one GiB.");
            token.ThrowIfCancellationRequested();
            await using var file = File.OpenRead(archive);
            using var content = new StreamContent(file);
            using var request = new HttpRequestMessage(HttpMethod.Put, ArchiveRoute(input.Container, parent) + "&noOverwriteDirNonDir=1");
            request.Content = content;
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
            started = true;
            using var response = await http.SendAsync(request, token); response.EnsureSuccessStatusCode();
            var after = await CheckContainerPathAsync(http, input.Container, input.Destination, false, token);
            if (after == null) throw new IOException("Container destination was not confirmed after extraction.");
            return await OperationOutput.ReportAsync(context, "Host path copied into the container; approved directory overwrite merges entries and does not remove unrelated entries.", new { input.Container, input.Source, input.Destination, input.Overwrite, after.Size });
        }
        catch (Exception exception) when (started && exception is HttpRequestException or IOException or OperationCanceledException)
        {
            await context.TraceAsync("Container extraction may have partially changed the destination. Verify before another request.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        finally { Directory.Delete(temporary, true); }
    }
    private static async Task BuildContainerArchiveAsync(string source, string name, string archive, string temporary, CancellationToken token)
    {
        await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write);
        await using var writer = new TarWriter(output, leaveOpen: true);
        if (!Directory.Exists(source))
        {
            if (new FileInfo(source).Length > 1073741824) throw new IOException("Container copy is limited to one GiB.");
            await using var data = File.OpenRead(source);
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite }, token);
            return;
        }
        var packed = Path.Combine(temporary, "tree.tar");
        await FilesystemOperations.PackDirectoryAsync(source, packed, 1073741824, token);
        await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, name) { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute }, token);
        await using var input = File.OpenRead(packed); await using var reader = new TarReader(input); TarEntry? entry;
        while ((entry = await reader.GetNextEntryAsync(cancellationToken: token)) != null)
        {
            entry.Name = name + "/" + entry.Name; entry.Mode &= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            await writer.WriteEntryAsync(entry, token);
        }
    }
}
