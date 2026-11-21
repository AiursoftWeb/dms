using System.CommandLine;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class ArtifactCommand(string action) : DmsCommandHandler
{
    protected override string Name => action;
    protected override string Description => action switch
    {
        "upload" => "Upload a local file or tar archive for use in a workflow.",
        "get" => "Show an artifact's file name, size, checksum, and expiration.",
        "download" => "Download an artifact to a local file and verify its SHA-256 checksum.",
        _ => throw new InvalidOperationException("Unknown artifact command.")
    };
    private readonly Option<Guid> _id = new("--id")
        { Required = true, Description = "ID of the artifact stored on the Controller." };
    private readonly Option<string> _file = new("--file")
    {
        Required = true,
        Description = action == "upload" ? "Path to the local file to upload." : "Local destination path."
    };
    private readonly Option<string> _kind = new("--kind")
    {
        DefaultValueFactory = _ => "file",
        Description = "Use file for a regular file or directory-tar for an archive to extract during import."
    };
    private readonly Option<bool> _overwrite = new("--overwrite")
        { Description = "Replace an existing local destination file." };
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
