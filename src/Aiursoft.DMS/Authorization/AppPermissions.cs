namespace Aiursoft.DMS.Authorization;

/// <summary>
/// A static class that provides all application permissions.
/// It uses a fake localizer to ensure permission names and descriptions are picked up by localization tools.
/// This class serves as the single source of truth for all permissions in the application.
/// </summary>
public class AppPermissions
{
    public const string Type = "Permission";

    /// <summary>
    /// A fake localizer that returns the input string as is.
    /// This is used to trick auto scanning tools to detect these strings for localization.
    /// </summary>
    public class FakeLocalizer
    {
        public string this[string name] => name;
    }

    public static List<PermissionDescriptor> GetAllPermissions()
    {
        // Make a fake localizer. This returns as is.
        // This trick is to make auto scanning tools to detect these strings for localization.
        var localizer = new FakeLocalizer();
        List<PermissionDescriptor> allPermission =
        [
            new(AppPermissionNames.CanRequestSafeRiskWorkflow, localizer["Request safe workflows"], localizer["Allows requesting safe workflows on any machine."]),
            new(AppPermissionNames.CanAutoApproveSafeRiskWorkflow, localizer["Automatically approve safe workflows"], localizer["Automatically approves requested safe workflows. Request permission is also required."]),
            new(AppPermissionNames.CanApproveSafeRiskWorkflow, localizer["Approve safe workflows"], localizer["Allows approving or denying safe workflow requests on any machine."]),
            new(AppPermissionNames.CanRequestLowRiskWorkflow, localizer["Request low-risk workflows"], localizer["Allows requesting low-risk workflows on any machine."]),
            new(AppPermissionNames.CanAutoApproveLowRiskWorkflow, localizer["Automatically approve low-risk workflows"], localizer["Automatically approves requested low-risk workflows. Request permission is also required."]),
            new(AppPermissionNames.CanApproveLowRiskWorkflow, localizer["Approve low-risk workflows"], localizer["Allows approving or denying low-risk workflow requests on any machine."]),
            new(AppPermissionNames.CanRequestMediumRiskWorkflow, localizer["Request medium-risk workflows"], localizer["Allows requesting medium-risk workflows on any machine."]),
            new(AppPermissionNames.CanAutoApproveMediumRiskWorkflow, localizer["Automatically approve medium-risk workflows"], localizer["Automatically approves requested medium-risk workflows. Request permission is also required."]),
            new(AppPermissionNames.CanApproveMediumRiskWorkflow, localizer["Approve medium-risk workflows"], localizer["Allows approving or denying medium-risk workflow requests on any machine."]),
            new(AppPermissionNames.CanRequestHighRiskWorkflow, localizer["Request high-risk workflows"], localizer["Allows requesting high-risk workflows on any machine."]),
            new(AppPermissionNames.CanAutoApproveHighRiskWorkflow, localizer["Automatically approve high-risk workflows"], localizer["Automatically approves requested high-risk workflows. Request permission is also required."]),
            new(AppPermissionNames.CanApproveHighRiskWorkflow, localizer["Approve high-risk workflows"], localizer["Allows approving or denying high-risk workflow requests on any machine."]),
            new(AppPermissionNames.CanRequestVeryHighRiskWorkflow, localizer["Request very-high-risk workflows"], localizer["Allows requesting very-high-risk workflows on any machine."]),
            new(AppPermissionNames.CanAutoApproveVeryHighRiskWorkflow, localizer["Automatically approve very-high-risk workflows"], localizer["Automatically approves requested very-high-risk workflows. Request permission is also required."]),
            new(AppPermissionNames.CanApproveVeryHighRiskWorkflow, localizer["Approve very-high-risk workflows"], localizer["Allows approving or denying very-high-risk workflow requests on any machine."]),
            new(AppPermissionNames.CanApproveOwnWorkflow, localizer["Approve own workflow requests"], localizer["Allows deciding your own requests when you also have approval permission for their risk level."]),
            new(AppPermissionNames.CanReadDmsOperations, localizer["Read DMS operations"], localizer["Allows viewing machines, workflow history, Lockboxes, traces and investigation records."]),
            new(AppPermissionNames.CanManageDmsAccess, localizer["Manage DMS access"], localizer["Manage inventory, enrollment keys and Agent identities. Workflow permissions are configured on roles."]),
            new(AppPermissionNames.CanReadUsers,
                localizer["Read Users"],
                localizer["Allows viewing the list of all users."]),
            new(AppPermissionNames.CanDeleteUsers,
                localizer["Delete Users"],
                localizer["Allows the permanent deletion of user accounts."]),
            new(AppPermissionNames.CanAddUsers,
                localizer["Add New Users"],
                    localizer["Grants permission to create new user accounts."]),
            new(AppPermissionNames.CanEditUsers,
                localizer["Edit User Information"],
                    localizer["Allows modification of user details like email and roles, and can also reset user passwords."]),
            new(AppPermissionNames.CanReadRoles,
                localizer["Read Roles"],
                    localizer["Allows viewing the list of roles and their assigned permissions."]),
            new(AppPermissionNames.CanDeleteRoles,
                localizer["Delete Roles"],
                localizer["Allows the permanent deletion of roles."]),
            new(AppPermissionNames.CanAddRoles,
                localizer["Add New Roles"],
                localizer["Grants permission to create new roles."]),
            new(AppPermissionNames.CanEditRoles,
                localizer["Edit Role Information"],
                localizer["Allows modification of role names and their assigned permissions."]),
            new(AppPermissionNames.CanAssignRoleToUser,
                localizer["Assign Roles to Users"],
                localizer["Allows assigning or removing roles for any user."]),
            new(AppPermissionNames.CanReadPermissions,
                localizer["Read Permissions"],
                localizer["Allows viewing the list of all permissions and their assignments to roles and users."]),
            new(AppPermissionNames.CanViewSystemContext,
                localizer["View System Context"],
                localizer["Allows viewing system-level information and settings."]),
            new(AppPermissionNames.CanRebootThisApp,
                localizer["Reboot This App"],
                localizer["Grants permission to restart the application instance. May cause availability interruptions but all settings and cache will be reloaded."]),
            new(AppPermissionNames.CanViewBackgroundJobs,
                localizer["View Background Jobs"],
                localizer["Allows viewing the background job dashboard and managing jobs."]),
            new(AppPermissionNames.CanManageGlobalSettings,
                localizer["Manage Global Settings"],
                localizer["Allows viewing and modifying global application settings."])
        ];
        return allPermission;
    }
}
