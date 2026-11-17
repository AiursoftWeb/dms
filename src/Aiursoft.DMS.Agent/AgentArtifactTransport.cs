using Aiursoft.DMS.Workflows.Operations;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Agent;

public sealed class AgentArtifactTransport(Uri controller, string key, Guid taskId) : IAgentArtifactTransport
{
    private readonly DmsArtifactClient _client = new(controller.ToString(), key, taskId);
    public Task<ArtifactReference> UploadAsync(Guid workflowId, string file, string name, string kind, CancellationToken token) => _client.UploadAsync(file, name, kind, token);
    public Task DownloadAsync(ArtifactReference artifact, string file, CancellationToken token) => _client.DownloadAsync(artifact, file, token: token);
}
