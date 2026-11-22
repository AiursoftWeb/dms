using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class DmsViewModel : UiStackLayoutViewModel
{
    public DmsViewModel() { PageTitle = "DMS"; }
    public List<Machine> Machines { get; set; } = [];
    public List<LockboxRequest> Lockboxes { get; set; } = [];
    public List<WorkflowRun> Runs { get; set; } = [];
    public List<PersonalApiKey> Keys { get; set; } = [];
    public WorkflowRun? Run { get; set; }
    public WorkflowCatalogResult? Catalog { get; set; }
    public StoredDmsArtifact? Artifact { get; set; }
    public string? ArtifactText { get; set; }
    public WorkflowAuditQuery AuditQuery { get; set; } = new();
    public WorkflowAuditPage? AuditPage { get; set; }
    public int AuditError { get; set; }
    public IReadOnlyList<TraceEntry> Trace { get; set; } = [];
    public string? Notice { get; set; }
    public string? NewKey { get; set; }
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public WorkflowOrigin? Origin { get; set; }
    public bool ClusterWorkflowsOnly { get; set; }
    public Guid? MachineId { get; set; }
    public string? Definition { get; set; }
    public static string Color(RunState state) => state switch
    {
        RunState.Running => "primary", RunState.Succeeded => "success", RunState.Failed => "danger",
        RunState.Warning or RunState.NeedsVerification or RunState.CancelRequested => "warning", _ => "secondary"
    };
}
