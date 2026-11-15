using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class DockerPsWorkflow(DockerPsInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<DockerPsInput, ProcessResult>(MachineWorkflows.DockerPs, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, DockerPsInput input, CancellationToken token)
    {
        input.Validate();
        List<string> arguments = ["--host", "unix:///var/run/docker.sock", "ps", "--no-trunc",
            "--format", "{{.ID}}\t{{.Names}}\t{{.Image}}\t{{.Status}}"];
        if (input.All) arguments.Add("--all");
        if (input.GrepName != null) arguments.AddRange(["--filter", $"name={Regex.Escape(input.GrepName)}"]);
        return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(
            new ProcessWorkflow(dockerExecutable, arguments, captureOutput: true), token))!;
    }
}

public sealed class DockerLogsWorkflow(DockerLogsInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<DockerLogsInput, ProcessResult>(MachineWorkflows.DockerLogs, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, DockerLogsInput input, CancellationToken token)
    {
        input.Validate();
        List<string> arguments = ["--host", "unix:///var/run/docker.sock", "logs", "--timestamps",
            "--tail", input.Tail.ToString(CultureInfo.InvariantCulture)];
        if (input.Since.HasValue) arguments.AddRange(["--since", input.Since.Value.ToString("O", CultureInfo.InvariantCulture)]);
        arguments.Add(input.Container);
        // Stream bounded log lines to the trace; do not collect an unbounded result in memory.
        return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(dockerExecutable, arguments), token))!;
    }
}

public sealed class DockerExecWorkflow(DockerExecInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<DockerExecInput, ProcessResult>(MachineWorkflows.DockerExec, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, DockerExecInput input, CancellationToken token)
    {
        input.Validate();
        token.ThrowIfCancellationRequested();
        try
        {
            return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(dockerExecutable,
                ["--host", "unix:///var/run/docker.sock", "exec", input.Container, .. input.Command]), token))!;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChildWorkflowException)
        {
            // A Docker client failure cannot distinguish a remote command failure from a lost connection.
            await context.TraceAsync("Docker exec did not confirm success. The container command may still be running. Check its logs before retrying.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }
}

public sealed class DockerKillWorkflow(DockerContainerInput input, string dockerExecutable = "/usr/bin/docker")
    : MachineWorkflow<DockerContainerInput, ProcessResult>(MachineWorkflows.DockerKill, input)
{
    public override async Task<ProcessResult> ExecuteAsync(MachineWorkflowContext context, DockerContainerInput input, CancellationToken token)
    {
        input.Validate();
        token.ThrowIfCancellationRequested();
        try
        {
            return JsonSerializer.Deserialize<ProcessResult>(await context.CallAsync(new ProcessWorkflow(dockerExecutable,
                ["--host", "unix:///var/run/docker.sock", "kill", input.Container], captureOutput: true), token))!;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChildWorkflowException)
        {
            await context.TraceAsync("Docker kill did not confirm success. Check the container state before retrying.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }
}
