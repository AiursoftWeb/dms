using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed partial class BoxDeployWorkflow(BoxDeployInput input, string root = "/swarm-vol", string git = "/usr/bin/git")
    : MachineWorkflow<BoxDeployInput, BoxDeployResult>(MachineWorkflows.BoxDeploy, input)
{
    // Keep credentials out of the query output. Only the five official Box origins are accepted.
    private const string Preflight = """
        set -euo pipefail
        repository=$1
        git=$2
        box_git() { "$git" -c "safe.directory=$repository" -C "$repository" "$@"; }
        [ "$(box_git rev-parse --show-toplevel)" = "$repository" ] || {
            echo 'The Box must be the root of its Git working tree.' >&2; exit 1;
        }
        origin=$(box_git config --get remote.origin.url)
        if [[ ! "${origin,,}" =~ ^(https://gitlab\.aiursoft\.com/aiursoft/|git@gitlab\.aiursoft\.com:aiursoft/)(boxforglobal|boxforproart|boxforchina|boxforhl|boxforarchive)(\.git)?$ ]]; then
            echo 'The repository origin must be an official Aiursoft Box repository.' >&2; exit 1;
        fi
        [ "$(box_git symbolic-ref --short HEAD)" = master ] || {
            echo 'The Box must be on its master branch.' >&2; exit 1;
        }
        box_git diff --quiet --ignore-submodules -- || { echo 'The Box has local tracked changes.' >&2; exit 1; }
        box_git diff --cached --quiet --ignore-submodules -- || { echo 'The Box has staged changes.' >&2; exit 1; }
        box_git rev-parse --verify HEAD
        """;

    // Values are separate process arguments; neither the path nor the filter becomes shell source.
    private const string Deploy = """
        set -euo pipefail
        cd -- "$1"
        shift
        exec /bin/bash ./deploy.sh "$@" < /dev/null
        """;

    public override async Task<BoxDeployResult> ExecuteAsync(MachineWorkflowContext context, BoxDeployInput value, CancellationToken token)
    {
        value.Validate();
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box deployment requires Linux.");
        var directory = Path.GetFullPath(root);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("The Box search root does not exist.");
        await using var deploymentLock = AcquireLock(directory);
        // Search direct children only: never walk Docker volumes or choose an arbitrary first match.
        var candidates = Directory.EnumerateDirectories(directory)
            .Where(path => Path.GetFileName(path).Contains("box", StringComparison.OrdinalIgnoreCase)
                && !IsLink(path) && Directory.Exists(Path.Combine(path, ".git"))
                && !IsLink(Path.Combine(path, ".git")) && File.Exists(Path.Combine(path, "deploy.sh"))
                && !IsLink(Path.Combine(path, "deploy.sh")))
            .Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Expected exactly one Box Git repository with deploy.sh directly under {directory}; found {candidates.Length}. No update or deployment was started.");
        var repository = candidates[0];
        await context.TraceAsync($"Selected Box {repository}. Starting stage {value.Stage} and all later stages; stage-4 filter: {value.Filter ?? "all stacks"}. Earlier stages and shared preparation are not filtered. Approval covers master at execution time.");
        var previous = await RevisionAsync(context, ["/bin/bash", "-c", Preflight, "dms-box-preflight", repository, git], token);
        await context.TraceAsync($"Updating Box master with fast-forward only; previous commit {previous}. Private environment files are retained.");
        await context.CallAsync(new ProcessWorkflow("/usr/bin/env", ["GIT_TERMINAL_PROMPT=0", git,
            "-c", "safe.directory=" + repository, "-C", repository, "pull", "--ff-only", "--no-rebase", "origin", "master"]), token);
        var commit = await RevisionAsync(context, [git, "-c", "safe.directory=" + repository, "-C", repository, "rev-parse", "--verify", "HEAD"], token);
        // A tracked script may have changed during the approved pull. It must still be a regular local file.
        if (!File.Exists(Path.Combine(repository, "deploy.sh")) || IsLink(Path.Combine(repository, "deploy.sh")))
            throw new InvalidOperationException("The updated Box has no regular deploy.sh. No deployment was started.");
        await context.TraceAsync($"Deploying Box commit {commit}; starting stage {value.Stage}; stage-4 filter: {value.Filter ?? "all stacks"}.");
        token.ThrowIfCancellationRequested();
        try
        {
            string[] arguments = ["-c", Deploy, "dms-box-deploy", repository, "--stage", value.Stage.ToString(CultureInfo.InvariantCulture)];
            if (value.Filter != null) arguments = [.. arguments, "--filter", value.Filter];
            await context.CallAsync(new ProcessWorkflow("/bin/bash", arguments), token);
        }
        catch (OperationCanceledException)
        {
            await context.TraceAsync("Box deployment was interrupted. Submitted Docker or host changes may still take effect. Verify the machine before another deployment; this workflow will not be replayed.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        catch (ChildWorkflowException)
        {
            await context.TraceAsync("The Box deployment script failed. Earlier steps may have completed; inspect the trace and service state before another request.", "error");
            throw;
        }
        await context.TraceAsync("Box deployment script completed. Swarm rollout and application health must be checked separately.");
        return new BoxDeployResult(repository, previous, commit, value.Stage, value.Filter, 0);
    }

    private static FileStream AcquireLock(string directory)
    {
        try { return new FileStream(Path.Combine(directory, ".dms-box-deploy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("Cannot acquire the machine deployment lock. Another deployment or a filesystem error prevents this request. No update or deployment was started.", exception); }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static async Task<string> RevisionAsync(MachineWorkflowContext context, string[] command, CancellationToken token)
    {
        var result = JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(command[0], command[1..], captureOutput: true), token))!;
        var revision = result.Output.Trim();
        if (!CommitId().IsMatch(revision)) throw new InvalidDataException("Git did not return one full commit ID. No deployment was started.");
        return revision;
    }

    [GeneratedRegex(@"\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z")]
    private static partial Regex CommitId();
}
