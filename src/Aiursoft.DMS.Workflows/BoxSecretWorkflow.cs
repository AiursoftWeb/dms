using System.Diagnostics;
using System.Text;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public interface IBoxSecretTransport
{
    string Fingerprint(string name, string value);
    Task<string> TakeSetValueAsync(Guid workflowId, CancellationToken token);
    Task PublishRawValueAsync(Guid workflowId, string value, CancellationToken token);
}

internal sealed class BoxSecretWorkflow<TInput, TResult>(
    MachineWorkflowDefinition<TInput, TResult> definition, TInput input, IBoxSecretTransport? transport,
    Func<BoxSecretFile, Guid, CancellationToken, Task<TResult>> execute, string root = "/swarm-vol", string git = "/usr/bin/git")
    : MachineWorkflow<TInput, TResult>(definition, input)
{
    public override async Task<TResult> ExecuteAsync(MachineWorkflowContext context, TInput value, CancellationToken token)
    {
        await using var guard = BoxDeployWorkflow.AcquireLock(root);
        var repository = BoxDeployWorkflow.FindRepository(root);
        await context.CallAsync(new ProcessWorkflow("/bin/bash",
            ["-c", BoxDeployWorkflow.Preflight, "dms-box-secret-preflight", repository, git, BoxDeployWorkflow.OfficialOriginPattern],
            captureOutput: true), token);
        await context.CallAsync(new ProcessWorkflow("/bin/bash",
            ["-c", """test ! -L "$1" && { test ! -e "$1" || test -f "$1"; }""",
                "dms-box-secret-file", Path.Combine(repository, ".env.prod")]), token);
        return await execute(new BoxSecretFile(repository, transport), context.Id, token);
    }
}

internal sealed class BoxSecretFile(string repository, IBoxSecretTransport? transport)
{
    private const int MaxFileBytes = 128 * 1024;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UTF8Encoding Encoding = new(false, true);
    private readonly string _path = Path.Combine(repository, ".env.prod");

    private IBoxSecretTransport Transport => transport ?? throw new InvalidOperationException("Box secret transfer is not available.");

    public BoxSecretsResult List()
    {
        var (_, values) = Read();
        return new BoxSecretsResult(values.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new BoxSecretEntry(x.Key, x.Value.Value.Length != 0)).ToArray());
    }

    public BoxSecretSummary Summary(string name)
    {
        var (_, values) = Read();
        var value = values.TryGetValue(name, out var entry) ? entry.Value : "";
        return new BoxSecretSummary(name, value.Length != 0, value.Length == 0 ? null : Transport.Fingerprint(name, value));
    }

    public async Task<BoxSecretResult> SetAsync(string name, Guid workflow, CancellationToken token)
    {
        var (lines, entries) = Read();
        if (!File.Exists(_path))
            throw new InvalidOperationException("Prepare the Box .env.prod file before setting a secret.");
        var value = await Transport.TakeSetValueAsync(workflow, token);
        new BoxSecretValue(value).Validate();
        if (entries.TryGetValue(name, out var entry))
            lines[entry.Line] = name + "=" + value;
        else
            lines.Add(name + "=" + value);
        await WriteAsync(lines, token);
        return new BoxSecretResult(name);
    }

    public async Task<BoxSecretResult> PublishAsync(string name, Guid workflow, CancellationToken token)
    {
        var (_, entries) = Read();
        if (!entries.TryGetValue(name, out var entry) || entry.Value.Length == 0)
            throw new InvalidOperationException("This Box secret is not configured.");
        new BoxSecretValue(entry.Value).Validate();
        await Transport.PublishRawValueAsync(workflow, entry.Value, token);
        return new BoxSecretResult(name);
    }

    private (List<string> Lines, Dictionary<string, (int Line, string Value)> Values) Read()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box secrets require Linux.");
        var file = new FileInfo(_path);
        if (file.LinkTarget != null || Directory.Exists(_path))
            throw new InvalidOperationException(".env.prod must be a regular file.");
        if (!file.Exists) return ([], new(StringComparer.Ordinal));
        if ((File.GetUnixFileMode(_path) & ~PrivateMode) != 0 || file.Length > MaxFileBytes)
            throw new InvalidOperationException(".env.prod must be owner-only and at most 128 KiB.");
        List<string> lines;
        try { lines = File.ReadAllLines(_path, Encoding).ToList(); }
        catch (DecoderFallbackException) { throw new InvalidDataException(".env.prod must be valid UTF-8."); }
        Dictionary<string, (int, string)> entries = new(StringComparer.Ordinal);
        for (var line = 0; line < lines.Count; line++)
        {
            var text = lines[line].TrimStart();
            if (text.Length == 0 || text.StartsWith('#')) continue;
            var delimiter = text.IndexOf('=');
            if (delimiter < 1) throw new InvalidDataException(".env.prod contains a malformed setting.");
            var name = text[..delimiter].TrimEnd();
            new BoxSecretInput(name).Validate();
            if (!entries.TryAdd(name, (line, text[(delimiter + 1)..])))
                throw new InvalidDataException(".env.prod contains duplicate keys.");
        }
        return (lines, entries);
    }

    private async Task WriteAsync(List<string> lines, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box secrets require Linux.");
        var data = Encoding.GetBytes(string.Join('\n', lines) + "\n");
        if (data.Length > MaxFileBytes) throw new InvalidOperationException(".env.prod exceeds 128 KiB.");
        var temporary = Path.Combine(repository, ".git", $".dms-env-prod-{Guid.NewGuid():N}");
        try
        {
            await using (var stream = new FileStream(temporary, new FileStreamOptions
                   { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateMode }))
            {
                File.SetUnixFileMode(temporary, PrivateMode);
                await stream.WriteAsync(data, token);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path))
            {
                var start = new ProcessStartInfo("/usr/bin/chown") { RedirectStandardError = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("--reference=" + _path);
                start.ArgumentList.Add("--");
                start.ArgumentList.Add(temporary);
                using var process = Process.Start(start) ?? throw new IOException("Cannot preserve the .env.prod owner.");
                await process.WaitForExitAsync(CancellationToken.None);
                if (process.ExitCode != 0) throw new IOException("Cannot preserve the .env.prod owner.");
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
