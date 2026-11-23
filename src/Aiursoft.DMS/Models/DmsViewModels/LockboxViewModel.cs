using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public sealed class LockboxViewModel : UiStackLayoutViewModel
{
    public required LockboxRequest Item { get; init; }
    public required LockboxStatus Status { get; init; }
    public required LockboxSnapshot Snapshot { get; init; }
    public required IReadOnlyDictionary<Guid, string> MachineNames { get; init; }
    public required IReadOnlyList<ArtifactReference> InputArtifacts { get; init; }
    public string? Notice { get; init; }
    public bool CanDecide { get; init; }
    public bool CanWithdraw { get; init; }
    public bool OwnRequestView { get; init; }
    public bool CanViewOperations { get; init; }
    public RunState? ExecutionState { get; init; }
    public AgentUpgradeReview? AgentUpgrade { get; init; }
    public IReadOnlyDictionary<Guid, AptPlanResult>? AptPlans { get; init; }
    public bool CanResubmit { get; init; }
}

public sealed record AgentUpgradeReview(IReadOnlyList<AgentRelease> Requested, IReadOnlyList<AgentRelease>? Available)
{
    public bool RequiresResubmission => Available is not null && !AgentReleaseStore.Matches(Requested, Available);
}
