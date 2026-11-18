using System.ComponentModel.DataAnnotations;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Entities;

public class Machine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(64)] public string KeyHash { get; set; } = string.Empty;
    [MaxLength(100)] public string AgentVersion { get; set; } = string.Empty;
    public Guid? InstallationId { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool Disabled { get; set; }
}

public class PersonalApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(450)] public string UserId { get; set; } = string.Empty;
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    [MaxLength(64)] public string KeyHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class LockboxRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClientRequestId { get; set; }
    [MaxLength(450)] public string RequesterId { get; set; } = string.Empty;
    [MaxLength(100)] public string Definition { get; set; } = string.Empty;
    [MaxLength(30)] public string Version { get; set; } = WorkflowNames.Version;
    public RiskLevel Risk { get; set; }
    public string Parameters { get; set; } = string.Empty;
    public string TargetMachines { get; set; } = "[]";
    [MaxLength(2000)] public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DecisionState Decision { get; set; }
    [MaxLength(450)] public string? ApproverId { get; set; }
    public bool Automatic { get; set; }
    public DateTime? DecidedAt { get; set; }
    [MaxLength(2000)] public string DecisionReason { get; set; } = string.Empty;
    public Guid? WorkflowId { get; set; }
    // Historical approval wrapper, retained only to keep old callstack and trace links valid.
    public Guid? LegacyApprovalWorkflowId { get; set; }
}

public class WorkflowRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RootId { get; set; }
    public Guid? ParentId { get; set; }
    public Guid LockboxId { get; set; }
    public Guid? MachineId { get; set; }
    [MaxLength(100)] public string Definition { get; set; } = string.Empty;
    public RunState State { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string Result { get; set; } = string.Empty;
    public string Input { get; set; } = "{}";
    [MaxLength(30)] public string Version { get; set; } = WorkflowNames.Version;
    public bool IsMachineTask { get; set; }
    public long LastSequence { get; set; }
}

public class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.UtcNow;
    [MaxLength(450)] public string ActorId { get; set; } = string.Empty;
    [MaxLength(100)] public string Action { get; set; } = string.Empty;
    public Guid? ResourceId { get; set; }
    public string Detail { get; set; } = string.Empty;
}
