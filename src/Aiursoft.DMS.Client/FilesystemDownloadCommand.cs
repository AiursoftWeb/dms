using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

internal sealed class FilesystemDownloadCommand(bool asRoot = false) : DmsCommandHandler
{
    private readonly Option<Guid[]> _machines = new(MachineWorkflowCli.MachineIdOption)
    {
        AllowMultipleArgumentsPerToken = false,
        Description = "Target machine ID. Omit when exactly one machine is enabled."
    };
    private readonly Option<string?> _path = new("--path")
        { Description = "Absolute path of the machine file or directory to download." };
    private readonly Option<string> _file = new("--file")
        { Required = true, Description = "Local destination path." };
    private readonly Option<bool> _recursive = new("--recursive")
        { DefaultValueFactory = _ => false, Description = "Include directory contents." };
    private readonly Option<long> _maxBytes = new("--max-bytes")
        { DefaultValueFactory = _ => 268435456, Description = "Maximum size of the downloaded artifact in bytes." };
    private readonly Option<bool> _overwrite = new("--overwrite")
        { DefaultValueFactory = _ => false, Description = "Replace an existing local destination file." };
    private readonly Option<string?> _reason = new(MachineWorkflowCli.ReasonOption)
        { Description = asRoot ? "Reason recorded in the Lockbox. Execution starts after approval." : "Optional reason recorded in the Lockbox." };
    private readonly Option<string?> _id = new("--id")
        { Description = "Retrieve and download an approved Lockbox (lb/<UUID>)." };

    protected override string Name => asRoot ? "download-as-root" : "download";
    protected override string Description => asRoot
        ? "Download a machine file or directory using Agent privileges to a local file."
        : "Download a machine file or directory readable by other users to a local file.";

    protected override IEnumerable<Option> GetActionOptions()
    {
        yield return _id;
        yield return _machines;
        yield return _path;
        yield return _file;
        yield return _recursive;
        yield return _maxBytes;
        yield return _overwrite;
        yield return _reason;
    }

    protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
    {
        var existing = context.GetValue(_id);
        var file = Path.GetFullPath(context.GetValue(_file)!);
        var overwrite = context.GetValue(_overwrite);
        var definition = asRoot ? OperationsWorkflows.FilesystemDownloadAsRoot : OperationsWorkflows.FilesystemDownload;

        Guid workflowId;
        if (!string.IsNullOrEmpty(existing))
        {
            if (!LockboxId.TryParse(existing, out var lbId))
                throw new ArgumentException("Expected a Lockbox ID in the form lb/<UUID>.");
            var lockboxResp = await client.GetLockboxAsync(lbId);
            var lockbox = (lockboxResp.Data as JObject)?.ToObject<LockboxDetails>()
                ?? throw new ArgumentException($"Lockbox {lbId} not found.");
            if (lockbox.WorkflowId == null)
                throw new InvalidOperationException($"Lockbox {lbId} has not been approved yet. Decision state: {lockbox.Decision}.");
            workflowId = lockbox.WorkflowId.Value;
        }
        else
        {
            var path = context.GetValue(_path);
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Provide the remote --path to download.");
            var machines = context.GetValue(_machines) ?? [];
            var recursive = context.GetValue(_recursive);
            var maxBytes = context.GetValue(_maxBytes);
            var reason = context.GetValue(_reason) ?? string.Empty;
            if (asRoot && string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required for privileged download-as-root requests.");

            var input = new ExportPathInput(path, recursive, maxBytes);
            var submitResponse = await client.SubmitAsync(new OperationRequest
            {
                Workflow = definition.Name,
                MachineIds = machines.ToList(),
                Reason = reason,
                Input = definition.Serialize(input)
            });

            var receipt = (submitResponse.Data as JObject)?.ToObject<LockboxReceipt>();
            if (receipt == null || receipt.WorkflowId == null)
            {
                // Lockbox requires approval (e.g. download-as-root)
                return submitResponse;
            }
            workflowId = receipt.WorkflowId.Value;
        }

        // Wait for workflow to finish and fetch artifact
        JObject? targetNode = null;
        var timeout = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < timeout)
        {
            var callstack = await client.CallstackAsync(workflowId);
            var nodes = (callstack.Data as JArray)?.OfType<JObject>().ToArray() ?? [];
            targetNode = nodes.FirstOrDefault(node => node.Value<string>("Definition") == definition.Name);
            var rootNode = nodes.FirstOrDefault(node => node.Value<string>("Id") == workflowId.ToString());

            if (targetNode != null)
            {
                var state = (RunState)targetNode.Value<int>("State");
                if (state.Terminal() || state == RunState.NeedsVerification)
                    break;
            }
            if (rootNode != null)
            {
                var rootState = (RunState)rootNode.Value<int>("State");
                if ((rootState.Terminal() || rootState == RunState.NeedsVerification) && targetNode == null)
                    break;
            }

            await Task.Delay(500);
        }

        if (targetNode == null)
            throw new InvalidOperationException("Workflow task did not execute.");

        var finalState = (RunState)targetNode.Value<int>("State");
        if (finalState != RunState.Succeeded && finalState != RunState.Warning)
            throw new InvalidOperationException($"Workflow failed with state {finalState}.");

        var resultJson = targetNode.Value<string>("Result") ?? "{}";
        var opResult = definition.ReadResult(resultJson);
        var artifact = opResult.Artifact ?? throw new InvalidOperationException("Workflow did not produce an artifact.");

        await client.Artifacts.DownloadAsync(artifact, file, overwrite);
        return new DmsResponse
        {
            Code = Code.ResultShown,
            Message = "Artifact saved and SHA256 verified.",
            Data = new { Artifact = artifact, LocalFile = file }
        };
    }
}
