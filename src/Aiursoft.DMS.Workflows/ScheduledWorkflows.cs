using System.Reflection;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Cronos;

namespace Aiursoft.DMS.Workflows;

public sealed record WorkflowSchedule(WorkflowDefinition Definition, string Expression, bool RunOnStartup)
{
    public CronExpression Cron { get; } = CronExpression.Parse(Expression, CronFormat.Standard);
    public string Id => Definition.Name;
    public bool Matches(DateTime utcMinute) => Cron.GetNextOccurrence(utcMinute.AddTicks(-1), TimeZoneInfo.Utc) == utcMinute;
}

public sealed partial class MachineWorkflowRegistry
{
    private readonly Dictionary<string, WorkflowSchedule> _schedules = new(StringComparer.Ordinal);
    public IReadOnlyCollection<WorkflowSchedule> Schedules => _schedules.Values;

    // Only the compiled, explicitly selected assembly is scanned. Never load user-supplied plugins.
    public void RegisterAttributed(params Type[] types)
    {
        foreach (var type in types)
        {
            if (type.IsAbstract || !typeof(MachineWorkflow).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) == null)
                throw new InvalidOperationException("Attributed workflows must be concrete, parameterless machine workflows.");
            var risk = WorkflowMetadata.Risk(type);
            var sample = (MachineWorkflow)Activator.CreateInstance(type)!;
            if (sample.Input != "{}") throw new InvalidOperationException("Scheduled workflows cannot require business input.");
            var definition = new MachineWorkflowDefinition<EmptyInput, JsonElement>(sample.Name, risk, sample.Version);
            if (string.IsNullOrWhiteSpace(sample.Name) || sample.Name.Length > 100 || sample.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(sample.Version) || sample.Version.Length > 30)
                throw new InvalidOperationException("Scheduled workflow names and versions must fit the shared catalog limits.");
            var attribute = type.GetCustomAttribute<RunCronAttribute>();
            if (attribute?.Expression.Length > 200)
                throw new InvalidOperationException("Schedule expressions cannot exceed 200 characters.");
            var schedule = attribute == null ? null : new WorkflowSchedule(definition, attribute.Expression, attribute.RunOnStartup);
            Register(definition, (_, _) => (MachineWorkflow)Activator.CreateInstance(type)!);
            if (schedule != null) _schedules.Add(definition.Name, schedule);
        }
    }
}

// A read-only production example: it exercises the same child runtime as manually requested work.
[WorkflowRisk(RiskLevel.Safe)]
[RunCron("*/5 * * * *", RunOnStartup = true)]
public sealed class ScheduledAgentStatusWorkflow : MachineWorkflow
{
    public override string Name => "machine.agent.scheduled-status";
    public override string Version => "1";
    public override async Task<string> ExecuteAsync(MachineWorkflowContext context, CancellationToken token)
    {
        await context.TraceAsync("Automatic Agent status inspection started.");
        var result = await context.CallAsync(MachineWorkflows.AgentStatus, new EmptyInput(), token);
        await context.TraceAsync("Automatic Agent status inspection completed; no machine configuration was changed.");
        return JsonSerializer.Serialize(result);
    }
}
