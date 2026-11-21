namespace Aiursoft.DMS.Authorization;

/// <summary>
/// Defines all permission keys as constants. This is the single source of truth.
/// </summary>
public static class AppPermissionNames
{
    // DMS operations (global within this Controller).
    public const string CanRequestOpenRiskWorkflow = nameof(CanRequestOpenRiskWorkflow);
    public const string CanRequestSafeRiskWorkflow = nameof(CanRequestSafeRiskWorkflow);
    public const string CanAutoApproveSafeRiskWorkflow = nameof(CanAutoApproveSafeRiskWorkflow);
    public const string CanApproveSafeRiskWorkflow = nameof(CanApproveSafeRiskWorkflow);
    public const string CanRequestLowRiskWorkflow = nameof(CanRequestLowRiskWorkflow);
    public const string CanAutoApproveLowRiskWorkflow = nameof(CanAutoApproveLowRiskWorkflow);
    public const string CanApproveLowRiskWorkflow = nameof(CanApproveLowRiskWorkflow);
    public const string CanRequestMediumRiskWorkflow = nameof(CanRequestMediumRiskWorkflow);
    public const string CanAutoApproveMediumRiskWorkflow = nameof(CanAutoApproveMediumRiskWorkflow);
    public const string CanApproveMediumRiskWorkflow = nameof(CanApproveMediumRiskWorkflow);
    public const string CanRequestHighRiskWorkflow = nameof(CanRequestHighRiskWorkflow);
    public const string CanAutoApproveHighRiskWorkflow = nameof(CanAutoApproveHighRiskWorkflow);
    public const string CanApproveHighRiskWorkflow = nameof(CanApproveHighRiskWorkflow);
    public const string CanRequestVeryHighRiskWorkflow = nameof(CanRequestVeryHighRiskWorkflow);
    public const string CanAutoApproveVeryHighRiskWorkflow = nameof(CanAutoApproveVeryHighRiskWorkflow);
    public const string CanApproveVeryHighRiskWorkflow = nameof(CanApproveVeryHighRiskWorkflow);
    public const string CanApproveOwnWorkflow = nameof(CanApproveOwnWorkflow);
    public const string CanReadDmsOperations = nameof(CanReadDmsOperations);
    public const string CanManageDmsAccess = nameof(CanManageDmsAccess);

    // User Management
    public const string CanReadUsers = nameof(CanReadUsers);
    public const string CanDeleteUsers = nameof(CanDeleteUsers);
    public const string CanAddUsers = nameof(CanAddUsers);
    public const string CanEditUsers = nameof(CanEditUsers);
    public const string CanAssignRoleToUser = nameof(CanAssignRoleToUser);

    // Role Management
    public const string CanReadRoles = nameof(CanReadRoles);
    public const string CanDeleteRoles = nameof(CanDeleteRoles);
    public const string CanAddRoles = nameof(CanAddRoles);
    public const string CanEditRoles = nameof(CanEditRoles);

    // Permission Management
    public const string CanReadPermissions = nameof(CanReadPermissions);

    // System Management
    public const string CanViewSystemContext = nameof(CanViewSystemContext);
    public const string CanRebootThisApp = nameof(CanRebootThisApp);
    public const string CanViewBackgroundJobs = nameof(CanViewBackgroundJobs);
    public const string CanManageGlobalSettings = nameof(CanManageGlobalSettings);
}
