using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public interface IAgentArtifactTransport
{
    Task<ArtifactReference> UploadAsync(Guid workflowId, string file, string name, string kind, CancellationToken token);
    Task DownloadAsync(ArtifactReference artifact, string file, CancellationToken token);
}

public sealed record NativeCommand(string Executable, IReadOnlyList<string> Arguments, int TimeoutSeconds = 60,
    string? StandardInput = null, IReadOnlyDictionary<string, string>? Environment = null, int MaxCharacters = 4194304,
    string? WorkingDirectory = null);
public sealed record NativeResult(int ExitCode, string Output, string Error);

// Injectable only by compiled hosts/tests, never by a submitted workflow parameter.
public sealed class OperationHost(string root = "/", Func<NativeCommand, CancellationToken, Task<NativeResult>>? native = null,
    Func<HttpClient>? dockerHttp = null)
{
    public static OperationHost Default { get; } = new();
    public string FilePath(string path) => root == "/" ? path : Path.Combine(root, path.TrimStart('/'));
    public string ProfilesPath => FilePath("/etc/aiursoft-dms-agent/database-connections.json");
    public HttpClient CreateDockerClient() => dockerHttp != null ? dockerHttp() : DockerOperations.ArchiveClient();
    public async Task<string> ReadAsync(string path, CancellationToken token, int maximum = 1048576)
    {
        await using var file = new FileStream(FilePath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new IOException("The requested metadata file exceeds its bounded read limit.");
        using var reader = new StreamReader(file);
        var value = await reader.ReadToEndAsync(token);
        if (value.Length > maximum) throw new IOException("The metadata grew beyond its bounded read limit.");
        return value;
    }
    public bool Exists(string path) => File.Exists(FilePath(path)) || Directory.Exists(FilePath(path));
    public Task<NativeResult> RunAsync(NativeCommand command, CancellationToken token) => native != null ? native(command, token) : RunNativeAsync(command, token);

    public static string Tool(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/usr/sbin", "/bin", "/sbin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        throw new NotSupportedException($"Required tool '{name}' is not installed. Install it in a separately approved package operation.");
    }

    private static async Task<NativeResult> RunNativeAsync(NativeCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var executable = command.Executable.StartsWith('/') ? command.Executable : Tool(command.Executable);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = command.StandardInput != null,
            WorkingDirectory = command.WorkingDirectory ?? string.Empty
        };
        foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["LC_ALL"] = "C.UTF-8";
        if (command.Environment != null)
            foreach (var pair in command.Environment) process.StartInfo.Environment[pair.Key] = pair.Value;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(command.TimeoutSeconds));
        process.Start();
        using var registration = deadline.Token.Register(static state => Kill((Process)state!), process);
        try
        {
            var output = DrainAsync(process.StandardOutput, command.MaxCharacters, deadline.Token, process);
            var error = DrainAsync(process.StandardError, Math.Min(command.MaxCharacters, 65536), deadline.Token, process);
            if (command.StandardInput != null)
            {
                await process.StandardInput.WriteAsync(command.StandardInput.AsMemory(), deadline.Token);
                process.StandardInput.Close();
            }
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token));
            return new NativeResult(process.ExitCode, await output, await error);
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            throw;
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, int maximum, CancellationToken token, Process process)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        try
        {
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                if (result.Length + count > maximum) throw new IOException("Command output exceeds the operation limit; narrow its input or export an artifact.");
                result.Append(buffer, 0, count);
            }
        }
        catch { Kill(process); throw; }
        return result.ToString();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}

public sealed class OperationWorkflow<TInput>(MachineWorkflowDefinition<TInput, OperationResult> definition,
    TInput input, Func<MachineWorkflowContext, TInput, AgentEnvironment, CancellationToken, Task<OperationResult>> body,
    AgentEnvironment environment) : MachineWorkflow<TInput, OperationResult>(definition, input)
{
    public override async Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, TInput value, CancellationToken token)
    {
        var seconds = value switch
        {
            SampleInput sample => sample.Seconds + 10, SmartTestInput smart => smart.WaitSeconds + 90,
            ScriptInput script => script.TimeoutSeconds + 10, DatabaseSqlInput sql => sql.TimeoutSeconds + 30,
            SwarmChangeInput service => service.TimeoutSeconds + 90,
            SwarmRollingRestartInput restart => restart.TimeoutSeconds + 90,
            RebootInput => 960, PoweroffInput poweroff => Math.Max(180, poweroff.DelaySeconds + 60),
            PackageInput or PackageUpgradeInput or AptApplyInput => 1980, CopyPathInput or ImportPathInput or ContainerCopyInput => 660,
            ExportPathInput or ContainerExportInput => 900,
            _ when Name == OperationsWorkflows.AptUpdate.Name => 630, _ => 180
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
        var elapsed = Stopwatch.StartNew();
        await context.TraceAsync($"Starting operation {Name}; execution_limit_seconds={seconds}.");
        var result = await body(context, value, environment, deadline.Token);
        if (!result.Complete && !context.HasWarnings) await context.WarningAsync(result.Summary);
        await context.TraceAsync($"Operation completed: {result.Summary} Complete={result.Complete && !context.HasWarnings}; elapsed_ms={elapsed.ElapsedMilliseconds}.");
        return context.HasWarnings ? result with { Complete = false } : result;
    }
}

public static class OperationOutput
{
    public static OperationHost Host(AgentEnvironment environment) => environment.Operations ?? OperationHost.Default;
    public static async Task<OperationResult> ReportAsync(MachineWorkflowContext context, string summary, object data,
        bool complete = true, ArtifactReference? artifact = null)
    {
        var json = JsonSerializer.Serialize(data);
        for (var offset = 0; offset < json.Length; offset += 4096)
            await context.TraceAsync(json.Substring(offset, Math.Min(4096, json.Length - offset)), "data");
        if (!complete) await context.WarningAsync(summary);
        return new OperationResult(summary, complete, artifact, json.Length <= 8192 ? JsonSerializer.Deserialize<JsonElement>(json) : null);
    }
    public static async Task<NativeResult> CommandAsync(MachineWorkflowContext context, AgentEnvironment environment,
        string tool, IReadOnlyList<string> arguments, CancellationToken token, bool change = false,
        int seconds = 60, string? stdin = null, IReadOnlyDictionary<string, string>? variables = null,
        bool traceOutput = true, int[]? allowedExitCodes = null, string? workingDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        await context.TraceAsync($"Executing fixed operation with {tool}; timeout {seconds}s.");
        // Resolve absence before the effect boundary. A missing tool did not perform a mutation.
        var host = Host(environment);
        var elapsed = Stopwatch.StartNew();
        var started = false;
        try
        {
            started = true;
            var result = await host.RunAsync(new NativeCommand(tool, arguments, seconds, stdin, variables, WorkingDirectory: workingDirectory), token);
            if (traceOutput)
            {
                await TraceTextAsync(context, result.Output);
                await TraceTextAsync(context, result.Error, "stderr");
            }
            await context.TraceAsync($"Fixed operation finished: tool={tool}; exit_code={result.ExitCode}; elapsed_ms={elapsed.ElapsedMilliseconds}; stdout_characters={result.Output.Length}; stderr_characters={result.Error.Length}; raw_output_traced={traceOutput}.");
            if (!(allowedExitCodes ?? [0]).Contains(result.ExitCode) && traceOutput)
                await context.TraceAsync("The fixed operation did not confirm success. Already emitted stdout/stderr is retained in this workflow trace; stdout may contain partial results.", "error");
            if (!(allowedExitCodes ?? [0]).Contains(result.ExitCode)) throw new IOException($"{tool} exited with code {result.ExitCode}.");
            return result;
        }
        catch (OperationCanceledException) when (!change)
        {
            await context.TraceAsync(token.IsCancellationRequested
                ? $"Fixed operation stopped: tool={tool}; cancellation requested by the caller or workflow deadline; elapsed_ms={elapsed.ElapsedMilliseconds}. Buffered native output may be unavailable."
                : $"Fixed operation stopped: tool={tool}; its {seconds}-second native process limit was reached; elapsed_ms={elapsed.ElapsedMilliseconds}. Buffered native output may be unavailable; narrow the operation scope before retrying.", "error");
            throw;
        }
        catch (Exception exception) when (change && started && exception is not (NotSupportedException or WorkflowNeedsVerificationException))
        {
            await context.TraceAsync("The change did not confirm completion. Inspect its actual effects before submitting another request; no automatic retry.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }
    public static async Task TraceTextAsync(MachineWorkflowContext context, string value, string source = "stdout")
    {
        for (var offset = 0; offset < value.Length; offset += 4096)
            await context.TraceAsync(value.Substring(offset, Math.Min(4096, value.Length - offset)), source);
    }
}
