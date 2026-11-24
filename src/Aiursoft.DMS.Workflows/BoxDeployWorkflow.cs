using System.Globalization;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class BoxDeployWorkflow(BoxDeployInput input, string root = "/swarm-vol", string git = "/usr/bin/git", string gitConfigurationPath = BoxGitConfiguration.FilePath)
    : MachineWorkflow<BoxDeployInput, BoxDeployResult>(MachineWorkflows.BoxDeploy, input)
{
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
        await using var deploymentLock = BoxRepository.AcquireLock(directory);
        var repository = BoxRepository.FindRepository(directory);
        await context.TraceAsync($"Selected Box {repository}. Starting stage {value.Stage} and all later stages; stage-4 filter: {value.Filter ?? "all stacks"}. Earlier stages and shared preparation are not filtered. Approval covers master at execution time.");
        var update = await BoxRepository.UpdateAsync(context, repository, git, gitConfigurationPath, token);
        // A tracked script may have changed during the approved pull. It must still be a regular local file.
        if (!File.Exists(Path.Combine(repository, "deploy.sh")) || BoxRepository.IsLink(Path.Combine(repository, "deploy.sh")))
            throw new InvalidOperationException("The updated Box has no regular deploy.sh. No deployment was started.");
        await context.TraceAsync($"Deploying Box commit {update.Commit}; starting stage {value.Stage}; stage-4 filter: {value.Filter ?? "all stacks"}.");
        token.ThrowIfCancellationRequested();
        try
        {
            string[] deploymentArguments = ["-c", Deploy, "dms-box-deploy", repository, "--stage", value.Stage.ToString(CultureInfo.InvariantCulture)];
            if (value.Filter != null) deploymentArguments = [.. deploymentArguments, "--filter", value.Filter];
            await context.CallAsync(new ProcessWorkflow("/bin/bash", deploymentArguments), token);
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
        return new BoxDeployResult(repository, update.PreviousCommit, update.Commit, value.Stage, value.Filter, 0);
    }
}
