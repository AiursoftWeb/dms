using System.Reflection;

namespace Aiursoft.DMS.Contracts;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Field, Inherited = false)]
public sealed class WorkflowRiskAttribute(RiskLevel risk) : Attribute
{
    public RiskLevel Risk { get; } = Enum.IsDefined(risk) ? risk : throw new ArgumentOutOfRangeException(nameof(risk));
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class WorkflowCommandAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Parameter)]
public sealed class WorkflowOptionAttribute(string name, string description) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; } = description;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RunCronAttribute(string expression) : Attribute
{
    public string Expression { get; } = expression;
    public bool RunOnStartup { get; set; }
    public bool ReconcileAfterInterruption { get; set; }
}

public static class WorkflowMetadata
{
    public static RiskLevel Risk(MemberInfo declaration) => declaration.GetCustomAttribute<WorkflowRiskAttribute>()?.Risk
        ?? throw new InvalidOperationException($"{declaration.Name} must declare a WorkflowRisk attribute.");

    public static RiskLevel Risk(Type owner, string field) => Risk(owner.GetField(field, BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException($"Unknown workflow declaration {owner.Name}.{field}."));
}

public enum WorkflowOrigin { Manual, Automatic }
public enum WorkflowTrigger { ManualRequest, Subworkflow, Cron, AgentStartup }

public sealed record ScheduledInvocation(string ScheduleId, string Expression, DateTime ScheduledAt, WorkflowTrigger Trigger);
