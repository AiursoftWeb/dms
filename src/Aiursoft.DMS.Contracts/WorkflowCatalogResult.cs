namespace Aiursoft.DMS.Contracts;

public sealed record WorkflowDefinitionSummary(string Name, string Version, WorkflowLocation Location,
    RiskLevel Risk, bool RequiresTargets, bool CanRequest, bool AutoApprove, bool CanApprove);

public sealed record WorkflowCatalogResult(bool CanApproveOwnRequests,
    IReadOnlyList<WorkflowDefinitionSummary> Definitions);
