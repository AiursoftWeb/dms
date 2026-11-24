using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class DmsMachineViewModel : UiStackLayoutViewModel
{
    public required DmsMachineRow Machine { get; init; }
    public WorkflowRun? BaselineRun { get; init; }
    public required DmsMachineSshRow SshAccess { get; init; }
    public SshLeaseReadiness SshLeaseReadiness { get; init; }
    public bool? ScheduledSshEnabled { get; init; }
    public bool CanRequestSshDisable { get; init; }
    public bool SshStateChanged { get; init; }
    public bool CanRequestSshLease { get; init; }
    public string? BaselineSummary => OperationViewModel.ReadSummary(BaselineRun?.Result);
    public IReadOnlyList<WorkflowRun> RecentRuns { get; init; } = [];
    public IReadOnlyDictionary<Guid, string> RequestedDefinitions { get; init; } = new Dictionary<Guid, string>();
}
