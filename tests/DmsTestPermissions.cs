using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.AspNetCore.Identity;

namespace Aiursoft.DMS.Tests;

internal static class DmsTestPermissions
{
    public static void DeactivatePreviousMachines(TemplateDbContext db)
    {
        foreach (var machine in db.Machines) machine.Disabled = true;
    }

    public static void Set(TemplateDbContext db, string user, string permission, bool enabled = true)
    {
        var existing = db.UserClaims.Where(c => c.UserId == user && c.ClaimType == AppPermissions.Type && c.ClaimValue == permission).ToList();
        existing.AddRange(db.UserClaims.Local.Where(c => c.UserId == user && c.ClaimType == AppPermissions.Type && c.ClaimValue == permission && !existing.Contains(c)));
        db.UserClaims.RemoveRange(existing);
        if (enabled) db.UserClaims.Add(new IdentityUserClaim<string> { UserId = user, ClaimType = AppPermissions.Type, ClaimValue = permission });
    }

    public static void Risk(TemplateDbContext db, string user, RiskLevel risk, bool request = false, bool automatic = false, bool approve = false)
    {
        Set(db, user, DmsPermissions.Request(risk), request);
        Set(db, user, DmsPermissions.AutoApprove(risk), automatic);
        Set(db, user, DmsPermissions.Approve(risk), approve);
        Set(db, user, AppPermissionNames.CanReadDmsOperations);
    }
}
