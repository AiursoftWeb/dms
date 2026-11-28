namespace Aiursoft.DMS.Contracts;

// Persisted numeric values are part of the Lockbox and audit contracts.
public enum RiskLevel { Safe = 0, Low = 1, Medium = 2, High = 3, VeryHigh = 4, Open = 5 }
// WaitingApproval and Denied retain their historical numeric values; new approvals use DecisionState only.
public enum RunState { WaitingApproval, Queued, Offered, Accepted, Running, Succeeded, Warning, Failed, Denied, CancelRequested, Cancelled, NeedsVerification }
public enum DecisionState { Pending, Approved, Denied, Withdrawn, Expired }

public static class WorkflowNames
{
    public const string Lockbox = "lockbox";
    public const string Batch = "cluster.machine-batch";
    public const string Performance = "machine.performance.info";
    public const string Restart = "machine.systemctl.restart";
    public const string AgentUpgrade = "machine.agent.upgrade";
    public const string PullerForceUpdate = "machine.box.puller.force-update";
    public const string AgentStatus = "machine.agent.status";
    public const string ApplySecurityBaseline = "machine.security.apply-baseline";
    public const string ServiceStatus = "machine.systemctl.status";
    public const string ListBlockDevices = "machine.layout.lsblk";
    public const string DiskFree = "machine.layout.df";
    public const string Process = "internal.process";
    public const string ListMachines = "cluster.get-machines";
    public const string ListArtifacts = "cluster.artifact.ls";
    public const string AddMachine = "cluster.add-machine";
    public const string Kill = "operation.kill-workflow";
    public const string Version = "2";

    public static bool Terminal(this RunState state) => state is RunState.Succeeded or RunState.Warning
        or RunState.Failed or RunState.Cancelled or RunState.Denied;
}

public sealed class OperationRequest
{
    public Guid RequestId { get; set; } = Guid.NewGuid();
    public string Workflow { get; set; } = string.Empty;
    public List<Guid> MachineIds { get; set; } = [];
    public bool AllMachines { get; set; }
    public string Input { get; set; } = "{}";
    public string MachineName { get; set; } = string.Empty;
    public Guid? TargetWorkflowId { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public sealed class WorkflowEvent
{
    public string Version { get; set; } = WorkflowNames.Version;
    public string Input { get; set; } = "{}";
    public long Sequence { get; set; }
    public Guid WorkflowId { get; set; }
    public Guid? ParentId { get; set; }
    public string Definition { get; set; } = string.Empty;
    public RunState State { get; set; }
    public bool IsTrace { get; set; }
    public string Source { get; set; } = "engine";
    public string Message { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public DateTime Time { get; set; } = DateTime.UtcNow;
}

public sealed class AgentMessage
{
    public ScheduledInvocation? Schedule { get; set; }
    public Guid InstallationId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public Guid TaskId { get; set; }
    public Guid? TargetWorkflowId { get; set; }
    public string Version { get; set; } = WorkflowNames.Version;
    public string Definition { get; set; } = string.Empty;
    public string Input { get; set; } = "{}";
    public RunState State { get; set; }
    public long Sequence { get; set; }
    public WorkflowEvent? Event { get; set; }
    public LockboxContext? Lockbox { get; set; }
}
