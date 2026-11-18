using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Tests;

// These examples are compiled by the tests, not registered by the product.
public sealed record UnitInput(long Units, bool Fail = false)
{
    public void Validate()
    {
        if (Units is < 1 or > 1000) throw new ArgumentException("Units must be between 1 and 1000.");
    }
}
public sealed record RepeatInput(long Units, int Copies, bool ProbeFailure = false)
{
    public void Validate()
    {
        new UnitInput(Units).Validate();
        if (Copies is < 1 or > 10) throw new ArgumentException("Copies must be between 1 and 10.");
    }
}
public sealed record UnitResult(long Units);
public sealed record RepeatedResult(long Units, Guid InstallationId);
public sealed record TotalResult(long Units, int Machines, List<Guid> Installations);

public static class AuthoringExamples
{
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<UnitInput, UnitResult> Unit = new(
        "example.unit", WorkflowMetadata.Risk(typeof(AuthoringExamples), nameof(Unit)), Version: "unit-1", ValidateInput: input => input.Validate());
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<RepeatInput, RepeatedResult> Repeat = new(
        "example.repeat", WorkflowMetadata.Risk(typeof(AuthoringExamples), nameof(Repeat)), "repeat-1", input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Medium)]
    public static readonly ClusterWorkflowDefinition<RepeatInput, TotalResult> Total = new(
        "example.total", WorkflowMetadata.Risk(typeof(AuthoringExamples), nameof(Total)), "total-1", input => input.Validate(), SelectMachines: true, PublicEntry: true);

    public static void RegisterMachines(MachineWorkflowRegistry registry)
    {
        registry.Register(Unit, (input, _) => new UnitWorkflow(input));
        registry.Register(Repeat, (input, environment) => new RepeatWorkflow(input, environment.InstallationId));
    }
    public static void RegisterCluster(ClusterWorkflowRegistry registry) => registry.Register(Total, () => new TotalWorkflow());
}

public sealed class UnitWorkflow(UnitInput input) : MachineWorkflow<UnitInput, UnitResult>(AuthoringExamples.Unit, input)
{
    public override Task<UnitResult> ExecuteAsync(MachineWorkflowContext context, UnitInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (input.Fail) throw new InvalidOperationException("An intentional example failure.");
        return Task.FromResult(new UnitResult(input.Units));
    }
}

public sealed class RepeatWorkflow(RepeatInput input, Guid installationId)
    : MachineWorkflow<RepeatInput, RepeatedResult>(AuthoringExamples.Repeat, input)
{
    public override async Task<RepeatedResult> ExecuteAsync(MachineWorkflowContext context, RepeatInput input, CancellationToken token)
    {
        long total = 0;
        for (var copy = 0; copy < input.Copies; copy++)
        {
            var unit = await context.CallAsync(AuthoringExamples.Unit, new UnitInput(input.Units), token);
            total += unit.Units;
        }
        return new RepeatedResult(total, installationId);
    }
}

public sealed class TotalWorkflow : ClusterWorkflow<RepeatInput, TotalResult>
{
    public override async Task<TotalResult> ExecuteAsync(WorkflowExecutionContext context, RepeatInput input, CancellationToken token)
    {
        long total = 0;
        var installations = new List<Guid>();
        foreach (var machine in await context.GetApprovedMachinesAsync(token))
        {
            if (input.ProbeFailure)
            {
                try { await context.CallMachineAsync(AuthoringExamples.Unit, machine, new UnitInput(input.Units, Fail: true), token); }
                catch (ChildWorkflowException failure)
                {
                    await context.TraceAsync($"Handled example failure in {failure.WorkflowId}.", token);
                }
            }
            var result = await context.CallMachineAsync(AuthoringExamples.Repeat, machine, input, token);
            total += result.Units;
            installations.Add(result.InstallationId);
        }
        return new TotalResult(total, installations.Count, installations);
    }
}
