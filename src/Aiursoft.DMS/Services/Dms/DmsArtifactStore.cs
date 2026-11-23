using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed record StoredDmsArtifact(ArtifactReference Reference, string OwnerId, Guid? MachineTaskId, DateTime CreatedAt, DateTime ExpiresAt, bool AuditInput = false);

public sealed partial class DmsArtifactStore(IConfiguration configuration)
{
    private readonly string _root = Path.Combine(Path.GetFullPath(configuration["Dms:StatePath"] ?? "dms-state"), "artifacts");
    private readonly SemaphoreSlim _writes = new(1, 1);
    private const long MaximumArtifact = 1073741824;
    private readonly long _maximumTotal = Capacity(configuration);
    private static long Capacity(IConfiguration configuration)
    {
        var value = configuration.GetValue<long?>("Dms:Artifacts:MaximumTotalBytes") ?? 10L * MaximumArtifact;
        if (value < MaximumArtifact || value > 1024L * MaximumArtifact) throw new InvalidOperationException("Artifact capacity must be between one GiB and one TiB.");
        return value;
    }
    private string DirectoryFor(Guid id) => Path.Combine(_root, id.ToString("N"));
    public StoredDmsArtifact Get(Guid id)
    {
        var metadata = Path.Combine(DirectoryFor(id), "artifact.json");
        if (!File.Exists(metadata)) throw new DmsException(404, "Artifact not found.");
        var value = JsonSerializer.Deserialize<StoredDmsArtifact>(File.ReadAllText(metadata)) ?? throw new InvalidDataException("Invalid artifact metadata.");
        if (value.Reference.Id != id || value.ExpiresAt <= DateTime.UtcNow) throw new DmsException(404, "Artifact expired or not found.");
        return value;
    }
    public FileStream Open(Guid id) => new(Path.Combine(DirectoryFor(id), "payload"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

    public async Task<List<ArtifactListItem>> ListOwnedAsync(string owner, ArtifactListInput input, CancellationToken token)
    {
        await _writes.WaitAsync(token);
        try
        {
            if (!Directory.Exists(_root)) return [];
            var now = DateTime.UtcNow;
            var owned = new List<StoredDmsArtifact>();
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                token.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)) continue;
                var file = Path.Combine(directory, "artifact.json");
                if (!File.Exists(file)) continue;
                await using var stream = File.OpenRead(file);
                var artifact = await JsonSerializer.DeserializeAsync<StoredDmsArtifact>(stream, cancellationToken: token);
                if (artifact is not null && artifact.OwnerId == owner && artifact.Reference.Id == id && artifact.ExpiresAt > now)
                    owned.Add(artifact);
            }

            return owned.OrderByDescending(artifact => artifact.CreatedAt).ThenBy(artifact => artifact.Reference.Id)
                .Skip(input.Offset).Take(input.Limit)
                .Select(artifact => new ArtifactListItem(artifact.Reference.Id, artifact.Reference.Name,
                    artifact.Reference.Length, artifact.AuditInput ? null : artifact.ExpiresAt))
                .ToList();
        }
        finally { _writes.Release(); }
    }

    private void Expire()
    {
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith(".upload-", StringComparison.Ordinal))
            { if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddHours(-24)) Directory.Delete(directory, true); continue; }
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            var file = Path.Combine(directory, "artifact.json");
            if (!File.Exists(file)) continue;
            var metadata = JsonSerializer.Deserialize<StoredDmsArtifact>(File.ReadAllText(file));
            if (metadata?.ExpiresAt <= DateTime.UtcNow) Directory.Delete(directory, true);
        }
    }
}
