using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class RemovePathWorkflow(RemovePathInput input, string executable = "/usr/bin/rm")
    : MachineWorkflow<RemovePathInput, ProcessResult>(MachineWorkflows.RemovePath, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, RemovePathInput value, CancellationToken token)
    {
        value.Validate();
        token.ThrowIfCancellationRequested();
        // Reject symlink parents instead of silently deleting in their destinations.
        for (var parent = Path.GetDirectoryName(value.Path); parent != null; parent = Path.GetDirectoryName(parent))
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Removal through a symbolic-link parent is not allowed.");
        var attributes = File.GetAttributes(value.Path);
        if (!value.Recursive && (attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            throw new IOException("Directory removal requires explicit recursive approval.");
        await context.TraceAsync("Removing the approved literal path permanently. No force or wildcard expansion. A symlink at the target is unlinked, not followed. Interrupted or partial deletion requires verification before any new request.");
        List<string> arguments = ["--verbose", "--one-file-system", "--preserve-root=all"];
        if (value.Recursive) arguments.Add("--recursive");
        arguments.AddRange(["--", value.Path]);
        token.ThrowIfCancellationRequested();
        try
        {
            return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(executable, arguments), token))!;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChildWorkflowException)
        {
            await context.TraceAsync("Removal did not confirm completion. Some entries may already be deleted. Inspect the approved path; do not automatically retry.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }
}
