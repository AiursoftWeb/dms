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

public sealed record ArtifactListInput(int Offset = 0, int Limit = 20)
{
    public void Validate()
    {
        if (Offset < 0) throw new ArgumentException("Artifact offset must be nonnegative.");
        // A 200-character name can occupy 1200 characters in JSON. Keep pages within the workflow result limit.
        OperationValidation.Limit(Limit, 20);
    }
}

public sealed record ArtifactListItem(Guid Id, string Name, long Bytes, DateTime? ExpiresAt);
