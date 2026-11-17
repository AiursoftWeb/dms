using System.Text.Json;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

// Browser and API callers share exactly the same artifact ownership and review checks.
public sealed class DmsArtifactAccess(DmsArtifactStore store, AccessService access, TemplateDbContext db, WorkflowCatalog catalog)
{
    public static bool Matches(ArtifactReference left, ArtifactReference right) =>
        left.Id == right.Id && left.Length == right.Length && left.Kind == right.Kind && left.Name == right.Name
        && left.Sha256.Equals(right.Sha256, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<ArtifactReference> Inputs(LockboxRequest box)
    {
        var operation = JsonSerializer.Deserialize<OperationRequest>(box.Parameters)!;
        return catalog.Find(operation.Workflow)?.Artifacts(operation.Input).ToArray() ?? [];
    }

    public async Task<StoredDmsArtifact> RequireHumanAsync(string actor, Guid id)
    {
        await access.RequireViewAsync(actor);
        var artifact = store.Get(id);
        if (artifact.OwnerId == actor) return artifact;
        var principal = await access.PrincipalAsync(actor);
        foreach (var box in await db.Lockboxes.AsNoTracking().Where(l => l.Parameters.Contains(id.ToString())).ToListAsync())
        {
            if (box.RequesterId == artifact.OwnerId && DmsPermissions.ForRisk(principal, box.Risk).CanApprove
                && Inputs(box).Any(frozen => Matches(frozen, artifact.Reference))) return artifact;
        }
        throw new DmsException(403, "Artifact is private and is not an input of a request you can review.");
    }
}
