using System.Security.Claims;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Authorization;

// Maps workflow risk levels to the existing Identity permission claims.
public static class DmsPermissions
{
    public static string Request(RiskLevel risk) => Name("Request", risk);
    public static string AutoApprove(RiskLevel risk) => Name("AutoApprove", risk);
    public static string Approve(RiskLevel risk) => Name("Approve", risk);
    private static string Name(string action, RiskLevel risk) => Enum.IsDefined(risk)
        ? $"Can{action}{risk}RiskWorkflow" : throw new ArgumentOutOfRangeException(nameof(risk));

    public static RiskPermission ForRisk(ClaimsPrincipal principal, RiskLevel risk)
    {
        var request = principal.HasClaim(AppPermissions.Type, Request(risk));
        if (risk == RiskLevel.Open) return new RiskPermission(risk, request, request, CanApprove: false);
        return new RiskPermission(risk, request,
            request && principal.HasClaim(AppPermissions.Type, AutoApprove(risk)),
            principal.HasClaim(AppPermissions.Type, Approve(risk)));
    }

    public static bool CanApproveOwn(ClaimsPrincipal principal) => principal.HasClaim(AppPermissions.Type, AppPermissionNames.CanApproveOwnWorkflow);
    public static bool CanDecide(ClaimsPrincipal principal, RiskLevel risk, bool ownRequest) =>
        ForRisk(principal, risk).CanApprove && (!ownRequest || CanApproveOwn(principal));

    public static CurrentUser Describe(ClaimsPrincipal principal) => new(
        principal.FindFirstValue(ClaimTypes.NameIdentifier)!, principal.Identity?.Name ?? "", CanApproveOwn(principal),
        principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().Order().ToList(),
        Enum.GetValues<RiskLevel>().OrderBy(risk => risk != RiskLevel.Open).Select(risk => ForRisk(principal, risk)).ToList(),
        principal.FindAll(AppPermissions.Type).Select(c => c.Value).Distinct().Order().ToList());
}
