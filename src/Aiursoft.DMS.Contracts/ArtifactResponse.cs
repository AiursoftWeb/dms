using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.DMS.Contracts;

public sealed class ArtifactResponse : AiurResponse
{
    public ArtifactReference? Data { get; set; }
}

public sealed record ArtifactUploadStatus(ArtifactReference Artifact, long Offset, bool Complete);
public sealed class ArtifactUploadResponse : AiurResponse
{
    public ArtifactUploadStatus? Data { get; set; }
}
