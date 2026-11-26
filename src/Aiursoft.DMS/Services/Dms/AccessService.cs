using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
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

    public async Task<ClaimsPrincipal> RequireViewAsync(string user)
    {
        var principal = await PrincipalAsync(user);
        if (!principal.HasClaim(AppPermissions.Type, AppPermissionNames.CanReadDmsOperations))
            throw new DmsException(403, "Read DMS operations permission is required.");
        return principal;
    }

    public async Task<IReadOnlyList<string>> EligibleApproversAsync(RiskLevel risk, string requesterId)
    {
        if (risk == RiskLevel.Open) return [];
        var approval = DmsPermissions.Approve(risk);
        const string selfApproval = AppPermissionNames.CanApproveOwnWorkflow;
        var direct = db.UserClaims.Select(claim => new { claim.UserId, claim.ClaimType, claim.ClaimValue });
        var viaRole = from membership in db.UserRoles
                      join claim in db.RoleClaims on membership.RoleId equals claim.RoleId
                      select new { membership.UserId, claim.ClaimType, claim.ClaimValue };
        var grants = direct.Concat(viaRole).Where(claim => claim.ClaimValue == approval || claim.ClaimValue == selfApproval);
        var candidates = await (from user in db.Users.AsNoTracking()
                                join claim in grants on user.Id equals claim.UserId
                                select new { user.Id, user.DisplayName, user.UserName, user.LockoutEnabled, user.LockoutEnd, claim.ClaimType, claim.ClaimValue })
            .NotCacheable().ToListAsync();

        var now = DateTimeOffset.UtcNow;
        return candidates.Where(candidate => string.Equals(candidate.ClaimType, AppPermissions.Type, StringComparison.OrdinalIgnoreCase) &&
                                              (!candidate.LockoutEnabled || candidate.LockoutEnd == null || candidate.LockoutEnd <= now))
            .GroupBy(candidate => candidate.Id)
            .Where(person => person.Any(candidate => candidate.ClaimValue == approval) &&
                             (person.Key != requesterId || person.Any(candidate => candidate.ClaimValue == selfApproval)))
            .Select(person => person.First())
            .Select(person => PersonName(person.DisplayName, person.UserName))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string PersonName(string displayName, string? userName) =>
        string.IsNullOrWhiteSpace(displayName) ? userName ?? "Unknown user" :
        string.IsNullOrWhiteSpace(userName) || string.Equals(displayName, userName, StringComparison.OrdinalIgnoreCase)
            ? displayName : $"{displayName} (@{userName})";

    public void Audit(string actor, string action, Guid? id = null, string detail = "") =>
        db.AuditEvents.Add(new AuditEvent { ActorId = actor, Action = action, ResourceId = id, Detail = detail });
}
