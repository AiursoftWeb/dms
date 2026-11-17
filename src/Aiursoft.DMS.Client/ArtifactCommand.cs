using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class ArtifactCommand(string action) : DmsCommandHandler
{
    protected override string Name => action;
    protected override string Description => "Transfer a private immutable artifact; machine reads/writes are separate approved workflows.";
    private readonly Option<Guid> _id = new("--id") { Required = true };
    private readonly Option<string> _file = new("--file") { Required = true };
    private readonly Option<string> _kind = new("--kind") { DefaultValueFactory = _ => "file", Description = "file or directory-tar; directory archives are validated before importing." };
    private readonly Option<bool> _overwrite = new("--overwrite");
    protected override IEnumerable<Option> GetActionOptions()
    {
        if (action != "upload") yield return _id;
        if (action != "get") yield return _file;
        if (action == "upload") yield return _kind;
        if (action == "download") yield return _overwrite;
    }
    protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
    {
        ArtifactReference artifact;
        if (action == "upload")
        {
            var file = Path.GetFullPath(context.GetValue(_file)!);
            artifact = await client.Artifacts.UploadAsync(file, Path.GetFileName(file), context.GetValue(_kind)!);
        }
        else
        {
            artifact = await client.Artifacts.GetAsync(context.GetValue(_id));
            if (action == "download") await client.Artifacts.DownloadAsync(artifact, context.GetValue(_file)!, context.GetValue(_overwrite));
        }
        return new DmsResponse { Code = Code.ResultShown, Message = action == "download" ? "Artifact saved and SHA256 verified." : "Artifact is private and expires after 24 hours.", Data = artifact };
    }
}
