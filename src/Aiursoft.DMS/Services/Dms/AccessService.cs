using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Entities;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AccessService(TemplateDbContext db, UserManager<User> users, IUserClaimsPrincipalFactory<User> principalFactory)
{
    // Resolve Identity claims for every decision, including enrollment delegated by a join-key issuer.
    public async Task<ClaimsPrincipal> PrincipalAsync(string user)
    {
        var actor = await users.FindByIdAsync(user);
        if (actor == null || await users.IsLockedOutAsync(actor)) throw new DmsException(403, "User is unavailable.");
        return await principalFactory.CreateAsync(actor);
    }

    public async Task RequireViewAsync(string user)
    {
        if (!(await PrincipalAsync(user)).HasClaim(AppPermissions.Type, AppPermissionNames.CanReadDmsOperations))
            throw new DmsException(403, "Read DMS operations permission is required.");
    }

    public void Audit(string actor, string action, Guid? id = null, string detail = "") =>
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = action, ResourceId = id, Detail = detail });
}
