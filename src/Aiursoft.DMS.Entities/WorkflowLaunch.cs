using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Entities;

// One immutable handoff per approved root. Children inherit its scope through RootId.
public sealed class WorkflowLaunch
{
    [Key] public Guid RootId { get; init; }
    [MaxLength(100)] public required string Definition { get; init; }
    [MaxLength(30)] public required string Version { get; init; }
    public required string Parameters { get; init; }
    public required string TargetMachines { get; init; }
    public Guid? EnrollmentId { get; init; }
    [MaxLength(450)] public required string RequesterId { get; init; }
    [MaxLength(450)] public string? ApproverId { get; init; }
    public bool Automatic { get; init; }
    public RiskLevel Risk { get; init; }
    public DateTime? DecidedAt { get; init; }

    public OperationRequest ReadParameters() => JsonSerializer.Deserialize<OperationRequest>(Parameters)!;
    public IReadOnlyList<Guid> ReadTargets() => JsonSerializer.Deserialize<Guid[]>(TargetMachines)!;

    public static WorkflowLaunch FromApproval(LockboxRequest request, Guid root, Guid? enrollmentId = null) => new()
    {
        RootId = root, Definition = request.Definition, Version = request.Version,
        Parameters = request.Parameters, TargetMachines = request.TargetMachines, EnrollmentId = enrollmentId,
        RequesterId = request.RequesterId, ApproverId = request.ApproverId, Automatic = request.Automatic,
        Risk = request.Risk, DecidedAt = request.DecidedAt
    };
}
