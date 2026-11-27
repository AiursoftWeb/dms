using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

internal static partial class BoxRepository
{
    // Keep credentials out of the query output. Only official Box origins are accepted.
    // GitLab advertises this SSH endpoint; retain the existing HTTPS and SCP forms.
    internal const string OfficialOriginPattern = @"^(https://gitlab\.aiursoft\.com/aiursoft/|git@gitlab\.aiursoft\.com:aiursoft/|ssh://git@ssh\.aiursoft\.com:2202/aiursoft/)(boxfor[a-zA-Z0-9_-]+)(\.git)?$";

    internal const string Preflight = """
        set -euo pipefail
        repository=$1
        git=$2
        box_git() { "$git" --no-optional-locks -c "safe.directory=$repository" -c core.fsmonitor=false -c core.hooksPath=/dev/null -C "$repository" "$@"; }
        [ "$(box_git rev-parse --show-toplevel)" = "$repository" ] || {
            echo 'The Box must be the root of its Git working tree.' >&2; exit 1;
        }
        origin=$(box_git config --get-all remote.origin.url)
        if [[ ! "${origin,,}" =~ $3 ]]; then
            echo 'The repository origin must be an official Aiursoft Box repository. Run box inspect and read its trace for sanitized details.' >&2; exit 1;
        fi
        [ "$(box_git symbolic-ref --short HEAD)" = master ] || {
            echo 'The Box must be on its master branch.' >&2; exit 1;
        }
        if box_git ls-files --error-unmatch .env.prod >/dev/null 2>&1; then
            echo 'Private .env.prod must not be tracked by Git.' >&2; exit 1;
        fi
        box_git diff --quiet --no-ext-diff --no-textconv --ignore-submodules -- || { echo 'The Box has local tracked changes.' >&2; exit 1; }
        box_git diff --cached --quiet --no-ext-diff --no-textconv --ignore-submodules -- || { echo 'The Box has staged changes.' >&2; exit 1; }
        box_git rev-parse --verify HEAD
        """;

    private const string Update = """
        set -euo pipefail
        repository=$1
        git=$2
        box_git() { "$git" -c "safe.directory=$repository" -c core.fsmonitor=false -c core.hooksPath=/dev/null -c submodule.recurse=false -C "$repository" "$@"; }
        box_git fetch --no-tags --no-recurse-submodules origin refs/heads/master
        target=$(box_git rev-parse --verify 'FETCH_HEAD^{commit}')
        tracked_environment=$(box_git ls-tree --name-only "$target" -- .env.prod)
        if [ -n "$tracked_environment" ]; then
            echo 'The remote Box tracks private .env.prod. No working tree update was performed.' >&2; exit 1
        fi
        box_git merge --ff-only --no-autostash "$target"
        """;

    internal static Task<string> PreflightRevisionAsync(
        MachineWorkflowContext context, string repository, string git, CancellationToken token) =>
        RevisionAsync(context, ["/bin/bash", "-c", Preflight, "dms-box-preflight", repository, git, OfficialOriginPattern], token);

    internal static async Task<BoxUpdateResult> UpdateAsync(
        MachineWorkflowContext context, string repository, string git, string gitConfigurationPath, CancellationToken token)
    {
        var previous = await PreflightRevisionAsync(context, repository, git, token);
        await context.TraceAsync($"Updating Box master with fast-forward only; previous commit {previous}. Private .env.prod is retained.");
        var identity = BoxGitConfiguration.Read(gitConfigurationPath);
        List<string> arguments = ["GIT_TERMINAL_PROMPT=0"];
        if (identity != null)
        {
            arguments.Add("GIT_SSH_COMMAND=" + BoxGitConfiguration.SshCommand(identity));
            arguments.Add("GIT_SSH_VARIANT=ssh");
            await context.TraceAsync("Git uses the configured machine Box deployment identity with strict host verification.");
        }
        arguments.AddRange(["/bin/bash", "-c", Update, "dms-box-update", repository, git]);
        await context.CallAsync(new ProcessWorkflow("/usr/bin/env", arguments), token);
        var commit = await RevisionAsync(context, [git, "-c", "safe.directory=" + repository, "-C", repository, "rev-parse", "--verify", "HEAD"], token);
        return new BoxUpdateResult(repository, previous, commit);
    }

    internal static string FindRepository(string root)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Box operations require Linux.");
        var directory = Path.GetFullPath(root);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("The Box search root does not exist.");
        // Search direct children only: never walk Docker volumes or choose an arbitrary first match.
        var candidates = Directory.EnumerateDirectories(directory)
            .Where(path => Path.GetFileName(path).StartsWith("boxfor", StringComparison.OrdinalIgnoreCase)
                && !IsLink(path) && Directory.Exists(Path.Combine(path, ".git"))
                && !IsLink(Path.Combine(path, ".git")) && File.Exists(Path.Combine(path, "deploy.sh"))
                && !IsLink(Path.Combine(path, "deploy.sh")))
            .Order(StringComparer.Ordinal).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Expected exactly one Box Git repository with deploy.sh directly under {directory}; found {candidates.Length}. No update or deployment was started.");
        return candidates[0];
    }

    internal static FileStream AcquireLock(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidOperationException("The Box search root does not exist.");
        try { return new FileStream(Path.Combine(directory, ".dms-box-deploy.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("Cannot acquire the Box lock. Another Box operation or a filesystem error prevents this request. No update or deployment was started.", exception); }
    }

    internal static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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
