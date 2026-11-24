using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class BoxUpdateWorkflow(string root = "/swarm-vol", string git = "/usr/bin/git", string gitConfigurationPath = BoxGitConfiguration.FilePath)
    : MachineWorkflow<EmptyInput, BoxUpdateResult>(MachineWorkflows.BoxUpdateOnly, new EmptyInput())
{
    public override async Task<BoxUpdateResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var directory = Path.GetFullPath(root);
        await using var updateLock = BoxRepository.AcquireLock(directory);
        var repository = BoxRepository.FindRepository(directory);
        await context.TraceAsync($"Selected Box {repository}. Approval covers master at execution time.");
        var result = await BoxRepository.UpdateAsync(context, repository, git, gitConfigurationPath, token);
        await context.TraceAsync($"Box checkout is at {result.Commit}. No deployment was started.");
        return result;
    }
}
