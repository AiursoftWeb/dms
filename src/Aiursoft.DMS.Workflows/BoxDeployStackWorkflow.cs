using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

public sealed partial class BoxDeployStackWorkflow(
    BoxDeployStackInput input,
    AgentEnvironment? agent = null,
    string root = "/swarm-vol",
    string git = "/usr/bin/git",
    string gitConfigurationPath = BoxGitConfiguration.FilePath)
    : MachineWorkflow<BoxDeployStackInput, BoxDeployStackResult>(MachineWorkflows.BoxDeployStack, input)
{
    private const int MaxComposeBytes = 1024 * 1024;
    private const int MaxEnvBytes = 128 * 1024;
    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    public override async Task<BoxDeployStackResult> ExecuteAsync(MachineWorkflowContext context, BoxDeployStackInput value, CancellationToken token)
    {
        value.Validate();
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box deployment requires Linux.");

        var directory = Path.GetFullPath(root);
        await using var deploymentLock = BoxRepository.AcquireLock(directory);
        var repository = BoxRepository.FindRepository(directory);

        string previousCommit;
        string commit;
        if (value.Pull)
        {
            await context.TraceAsync($"Selected Box {repository}. Updating master before deploying single stack '{value.Stack}'.");
            var update = await BoxRepository.UpdateAsync(context, repository, git, gitConfigurationPath, token);
            previousCommit = update.PreviousCommit;
            commit = update.Commit;
        }
        else
        {
            await context.TraceAsync($"Selected Box {repository}. Deploying single stack '{value.Stack}' from current checkout without git pull.");
            previousCommit = await BoxRepository.PreflightRevisionAsync(context, repository, git, token);
            commit = previousCommit;
        }

        var composePath = ResolveComposePath(repository, value.Stack);
        var relativeCompose = Path.GetRelativePath(repository, composePath);
        var template = await ReadRegularUtf8FileAsync(composePath, MaxComposeBytes, token);
        var variables = await LoadEnvironmentVariablesAsync(repository, token);
        var rendered = RenderCompose(template, variables, value.Stack);

        var stackDirectory = Path.GetDirectoryName(composePath)!;
        var host = agent?.Operations ?? OperationHost.Default;
        var secretsToScrub = variables.Values
            .Where(v => v.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToArray();

        await context.TraceAsync($"Deploying stage-4 stack '{value.Stack}' from {relativeCompose} at commit {commit} (in-memory template render, skipping image builds).");
        token.ThrowIfCancellationRequested();

        var elapsed = Stopwatch.StartNew();
        var affected = false;
        try
        {
            affected = true;
            var result = await host.RunAsync(new NativeCommand(
                "docker",
                DockerOperations.Arguments("stack", "deploy", "--detach", "--prune", "--with-registry-auth", "-c", "-", value.Stack),
                TimeoutSeconds: 300,
                StandardInput: rendered,
                WorkingDirectory: stackDirectory), token);

            var safeOut = ScrubSecrets(result.Output, secretsToScrub);
            var safeErr = ScrubSecrets(result.Error, secretsToScrub);
            if (safeOut.Length > 0) await OperationOutput.TraceTextAsync(context, safeOut);
            if (safeErr.Length > 0) await OperationOutput.TraceTextAsync(context, safeErr, "stderr");
            await context.TraceAsync($"docker stack deploy finished: stack={value.Stack}; exit_code={result.ExitCode}; elapsed_ms={elapsed.ElapsedMilliseconds}.");
            if (result.ExitCode != 0)
                throw new IOException($"docker stack deploy exited with code {result.ExitCode}.");
        }
        catch (Exception exception) when (affected && exception is not (NotSupportedException or WorkflowNeedsVerificationException))
        {
            await context.TraceAsync("Stack deployment did not confirm completion. Swarm services in this stack may already have changed; inspect service rollout status before retrying.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }

        await context.TraceAsync($"Stack '{value.Stack}' deployment command completed. Swarm rollout and application health must be checked separately.");
        return new BoxDeployStackResult(repository, previousCommit, commit, value.Stack, relativeCompose, 0, HealthVerified: false);
    }

    internal static string ResolveComposePath(string repository, string stack)
    {
        var candidates = new[]
        {
            Path.Combine(repository, "stage4", "stacks", stack, "docker-compose.yml"),
            Path.Combine(repository, "stage4", stack, "docker-compose.yml")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (!full.StartsWith(repository.TrimEnd('/') + "/", StringComparison.Ordinal))
                throw new IOException("Resolved compose file escapes the Box repository.");
            if (!File.Exists(full)) continue;
            SafeOperationPaths.Check(full);
            if (Directory.Exists(full))
                throw new IOException($"Expected a regular compose file at {full}.");
            return full;
        }

        throw new FileNotFoundException($"No stage-4 docker-compose.yml found for stack '{stack}' under stage4/stacks/{stack} or stage4/{stack}.");
    }

    internal static async Task<Dictionary<string, string>> LoadEnvironmentVariablesAsync(string repository, CancellationToken token)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var fileName in new[] { ".env", ".env.prod" })
        {
            var path = Path.Combine(repository, fileName);
            if (!File.Exists(path)) continue;
            SafeOperationPaths.Check(path);
            if (Directory.Exists(path))
                throw new InvalidOperationException($"{fileName} must be a regular file.");
            var content = await ReadRegularUtf8FileAsync(path, MaxEnvBytes, token);
            ParseEnvInto(content, values);
        }
        return values;
    }

    internal static void ParseEnvInto(string content, Dictionary<string, string> destination)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var delimiter = trimmed.IndexOf('=');
            if (delimiter < 1) continue;
            var key = trimmed[..delimiter].TrimEnd();
            var value = trimmed[(delimiter + 1)..];
            destination[key] = value;
        }
    }

    internal static string RenderCompose(string template, IReadOnlyDictionary<string, string> variables, string stack)
    {
        HashSet<string> missing = new(StringComparer.Ordinal);
        var rendered = PlaceholderPattern().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (variables.TryGetValue(key, out var value))
                return value;
            missing.Add(key);
            return match.Value;
        });

        if (missing.Count > 0)
            throw new InvalidOperationException($"Cannot deploy stack '{stack}': missing Box environment key(s): {string.Join(", ", missing.Order(StringComparer.Ordinal))}.");
        return rendered;
    }

    private static async Task<string> ReadRegularUtf8FileAsync(string path, int maxBytes, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null || !info.Exists)
            throw new IOException($"File '{Path.GetFileName(path)}' must be an existing regular file.");
        if (info.Length > maxBytes)
            throw new IOException($"File '{Path.GetFileName(path)}' exceeds the maximum supported size of {maxBytes} bytes.");
        return await File.ReadAllTextAsync(path, Utf8Strict, token);
    }

    private static string ScrubSecrets(string text, string[] secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets.Length == 0) return text;
        var result = text;
        foreach (var secret in secrets)
            result = result.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return result;
    }

    [GeneratedRegex(@"\{\{([A-Za-z_][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();
}
