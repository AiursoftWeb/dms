using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.DMS.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class SubworkflowTests : TestBase
{
    private sealed class ExampleWorkflow(Func<WorkflowExecutionContext, CancellationToken, Task<List<Guid>>> execute)
        : ClusterWorkflow<EmptyInput, List<Guid>>
    {
        public override Task<List<Guid>> ExecuteAsync(WorkflowExecutionContext context, EmptyInput input, CancellationToken token) => execute(context, token);
    }

    private async Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        await Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(WorkflowRun Parent, Machine[] Machines, string[] Keys)> ArrangeAsync(
        Func<WorkflowExecutionContext, CancellationToken, Task<List<Guid>>> execute, RunState initial = RunState.Queued)
    {
        var definition = new ClusterWorkflowDefinition<EmptyInput, List<Guid>>("test.cluster." + Guid.NewGuid().ToString("N"), RiskLevel.Safe);
        Server!.Services.GetRequiredService<ClusterWorkflowRegistry>().Register(definition, () => new ExampleWorkflow(execute));
        return await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);

            var keys = Enumerable.Range(0, 2).Select(_ => DmsKeyAuthenticationHandler.CreateKey(true)).ToArray();
            var machines = keys.Select((key, index) => new Machine { Name = $"node-{index}", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }).ToArray();
            var box = new LockboxRequest { WorkflowId = Guid.NewGuid(), ClientRequestId = Guid.NewGuid(), Decision = DecisionState.Approved, TargetMachines = JsonSerializer.Serialize(machines.Select(m => m.Id)), Definition = definition.Name };
            var root = new WorkflowRun { Id = box.WorkflowId!.Value, RootId = box.WorkflowId.Value, LockboxId = box.Id, Definition = WorkflowNames.Lockbox, State = RunState.Running };
            var parent = new WorkflowRun { RootId = root.Id, ParentId = root.Id, LockboxId = box.Id, Definition = definition.Name, Input = definition.Serialize(new EmptyInput()), State = initial };
            box.WorkflowId = parent.Id;
            db.WorkflowLaunches.Add(WorkflowLaunch.FromApproval(box, root.Id));
            db.Machines.AddRange(machines); db.Lockboxes.Add(box); db.WorkflowRuns.AddRange(root, parent);
            await db.SaveChangesAsync();
            return (parent, machines, keys);
        });
    }

    private async Task<WorkflowRun> WaitAsync(Guid id, Func<WorkflowRun, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            var run = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == id));
            if (predicate(run)) return run;
            await Task.Delay(50, timeout.Token);
        }
    }

    private async Task<WorkflowRun> NextChildAsync(Guid parent, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (true)
        {
            var children = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.ParentId == parent).OrderBy(r => r.CreatedAt).ToListAsync());
            if (children.Count >= count) return children[count - 1];
            await Task.Delay(50, timeout.Token);
        }
    }

    private Task CompleteAsync(Guid id, RunState state, object result) => DatabaseAsync(async db =>
    {
        var run = await db.WorkflowRuns.SingleAsync(r => r.Id == id);
        run.State = state; run.Result = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync();
        return true;
    });

    [TestMethod]
    public async Task ClusterAwaitsTwoRealAgentsAndNestedClusterWithIndependentIds()
    {
        // This test reads the complete inventory. Keep machines from previous fixtures
        // from consuming the workflow result limit before the two test agents join.
        await DatabaseAsync(async db =>
        {
            db.Machines.RemoveRange(db.Machines);
            await db.SaveChangesAsync();
            return true;
        });
        var nestedDefinition = new ClusterWorkflowDefinition<EmptyInput, List<Guid>>("test.nested", RiskLevel.Safe);
        Server!.Services.GetRequiredService<ClusterWorkflowRegistry>().Register(nestedDefinition, () => new ExampleWorkflow(async (context, token) =>
        {
            var results = new List<Guid>();
            foreach (var machine in await context.GetApprovedMachinesAsync(token))
            {
                var status = await context.CallMachineAsync(MachineWorkflows.AgentStatus, machine, new EmptyInput(), token);
                Assert.IsFalse(string.IsNullOrWhiteSpace(status.Version));
                Assert.IsGreaterThanOrEqualTo(1, status.ActiveTasks); Assert.IsGreaterThanOrEqualTo(0, status.PendingEvents);
                results.Add(status.InstallationId);
                var performance = await context.CallMachineAsync(MachineWorkflows.Performance, machine, new EmptyInput(), token);
                Assert.AreEqual(Environment.MachineName, performance.Hostname);
                Assert.AreEqual(Environment.ProcessorCount, performance.Processors);
                StringAssert.Contains(performance.Memory, "MemTotal:"); Assert.IsFalse(string.IsNullOrWhiteSpace(performance.Load));
            }
            return results;
        }));
        var setup = await ArrangeAsync(async (context, token) =>
        {
            var installations = await context.CallAsync(nestedDefinition, new EmptyInput(), token);
            var inventory = await context.CallAsync(ClusterWorkflows.GetMachines, new EmptyInput(), token);
            var approved = await context.GetApprovedMachinesAsync(token);
            var selected = inventory.Where(machine => approved.Contains(machine.Id)).ToList();
            Assert.HasCount(2, selected);
            foreach (var machine in selected)
            {
                Assert.AreNotEqual(Guid.Empty, machine.Id); StringAssert.StartsWith(machine.Name, "node-");
                Assert.IsFalse(string.IsNullOrWhiteSpace(machine.AgentVersion)); Assert.IsNotNull(machine.LastSeen);
                Assert.IsFalse(machine.Disabled);
            }
            return installations;
        });
        var paths = setup.Machines.Select(_ => Directory.CreateTempSubdirectory("dms-await-agent-").FullName).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var journals = paths.Select(path => new TaskJournal(path)).ToArray();
        var agents = setup.Keys.Select((key, index) => new AgentClient(Http.BaseAddress!, key, journals[index]).RunAsync(stop.Token)).ToArray();
        try
        {
            var run = await WaitAsync(setup.Parent.Id, r => r.State.Terminal());
            Assert.AreEqual(RunState.Succeeded, run.State, run.Result);
            CollectionAssert.AreEquivalent(journals.Select(j => j.InstallationId).ToList(), JsonSerializer.Deserialize<List<Guid>>(run.Result)!);
            var tree = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == setup.Parent.RootId).ToListAsync());
            Assert.AreNotEqual(Guid.Empty, setup.Parent.RootId);
            var root = tree.Single(r => r.Id == setup.Parent.RootId);
            Assert.AreEqual(root.Id, root.RootId); Assert.IsNull(root.ParentId);
            Assert.AreEqual(root.Id, setup.Parent.ParentId);
            var nested = tree.Single(r => r.Definition == nestedDefinition.Name);
            Assert.AreEqual(setup.Parent.Id, nested.ParentId);
            var children = tree.Where(r => r.IsMachineTask).ToList();
            Assert.HasCount(4, children); Assert.HasCount(tree.Count, tree.Select(r => r.Id).Distinct());
            Assert.IsTrue(children.All(c => c.ParentId == nested.Id && c.RootId == setup.Parent.RootId && c.State == RunState.Succeeded));
        }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(agents); } catch (OperationCanceledException) { }
            foreach (var journal in journals) journal.Dispose();
            foreach (var path in paths) Directory.Delete(path, true);
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task KnownChildFailureCanBeCaughtWithoutOverwritingParent(int outcome)
    {
        var handled = outcome != 0;
        var setup = await ArrangeAsync(async (context, token) =>
        {
            var machines = await context.GetApprovedMachinesAsync(token);
            try { await context.CallMachineAsync(MachineWorkflows.ServiceStatus, machines[0], new ServiceInput("first.service"), token); }
            catch (ChildWorkflowException exception) when (handled)
            {
                Assert.AreEqual(WorkflowNames.ServiceStatus, exception.Definition);
                Assert.AreEqual(machines[0], exception.MachineId); Assert.AreEqual(RunState.Failed, exception.State);
            }
            if (handled)
            {
                var second = await context.CallMachineAsync(MachineWorkflows.ServiceStatus, machines[0], new ServiceInput("second.service"), token);
                Assert.AreEqual("second", second.Output);
                if (outcome == 2) await context.WarningAsync(token);
            }
            return machines.ToList();
        });
        var first = await NextChildAsync(setup.Parent.Id, 1);
        // This query also proves the waiting parent does not hold ControllerGate.
        Assert.AreEqual("first.service", JsonSerializer.Deserialize<ServiceInput>(first.Input)!.Service);
        await CompleteAsync(first.Id, RunState.Failed, new { Error = "Known failure" });
        if (handled)
        {
            var second = await NextChildAsync(setup.Parent.Id, 2);
            Assert.AreNotEqual(first.Id, second.Id); Assert.AreEqual(first.ParentId, second.ParentId);
            Assert.AreEqual("second.service", JsonSerializer.Deserialize<ServiceInput>(second.Input)!.Service);
            await CompleteAsync(second.Id, RunState.Succeeded, new ProcessResult(0, "second"));
        }
        var run = await WaitAsync(setup.Parent.Id, r => r.State.Terminal());
        Assert.AreEqual(outcome == 2 ? RunState.Warning : handled ? RunState.Succeeded : RunState.Failed, run.State, run.Result);
        await Task.Delay(1200);
        Assert.AreEqual(run.State, (await WaitAsync(run.Id, _ => true)).State);
        Assert.AreEqual(RunState.Failed, (await WaitAsync(first.Id, _ => true)).State);
    }

    [TestMethod]
    public async Task UnknownChildCannotBeHiddenByCatchOrLateSuccess()
    {
        var setup = await ArrangeAsync(async (context, token) =>
        {
            try { await context.CallMachineAsync(MachineWorkflows.AgentStatus, (await context.GetApprovedMachinesAsync(token))[0], new EmptyInput(), token); }
            catch (WorkflowNeedsVerificationException) { }
            return [];
        });
        var child = await NextChildAsync(setup.Parent.Id, 1);
        await CompleteAsync(child.Id, RunState.NeedsVerification, new { Error = "Disconnected after start" });
        await WaitAsync(setup.Parent.Id, r => r.State == RunState.NeedsVerification);
        await CompleteAsync(child.Id, RunState.Succeeded, new AgentStatusResult(Guid.NewGuid(), "test", 0, 0));
        await Task.Delay(1200);
        Assert.AreEqual(RunState.NeedsVerification, (await WaitAsync(setup.Parent.Id, _ => true)).State);
    }

    [TestMethod]
    public async Task ChildCannotExpandFrozenTargetsOrDispatchInternalProcesses()
    {
        var added = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var setup = await ArrangeAsync(async (context, token) =>
        {
            var late = await added.Task.WaitAsync(token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.CallMachineAsync(MachineWorkflows.AgentStatus, late, new EmptyInput(), token));
            var forbidden = new MachineWorkflowDefinition<EmptyInput, ProcessResult>(WorkflowNames.Process, RiskLevel.VeryHigh);
            var approved = await context.GetApprovedMachinesAsync(token);
            await Assert.ThrowsAsync<InvalidDataException>(() => context.CallMachineAsync(forbidden, approved[0], new EmptyInput(), token));
            return (await context.GetApprovedMachinesAsync(token)).ToList();
        });
        var lateMachine = await DatabaseAsync(async db =>
        {
            var machine = new Machine { Name = "Added after approval" };
            db.Machines.Add(machine); await db.SaveChangesAsync(); return machine.Id;
        });
        added.SetResult(lateMachine);
        var run = await WaitAsync(setup.Parent.Id, r => r.State.Terminal());
        Assert.AreEqual(RunState.Succeeded, run.State, run.Result);
        Assert.HasCount(2, JsonSerializer.Deserialize<List<Guid>>(run.Result)!);
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => r.ParentId == run.Id)));
    }
    [TestMethod]
    public async Task RestartKeepsStartedParentUncertainButResumesQueuedParent()
    {
        var startedCount = 0;
        var started = await ArrangeAsync((_, _) => { Interlocked.Increment(ref startedCount); return Task.FromResult(new List<Guid>()); }, RunState.Running);
        var queuedCount = 0;
        await DatabaseAsync(async db =>
        {
            await Server!.Services.GetRequiredService<ClusterWorkflowRuntime>().RecoverAsync(db, CancellationToken.None);
            return true;
        });
        var queued = await ArrangeAsync((_, _) => { Interlocked.Increment(ref queuedCount); return Task.FromResult(new List<Guid>()); });
        var run = await WaitAsync(queued.Parent.Id, r => r.State.Terminal());
        Assert.AreEqual(RunState.Succeeded, run.State); Assert.AreEqual(1, queuedCount);
        Assert.AreEqual(RunState.NeedsVerification, (await WaitAsync(started.Parent.Id, _ => true)).State);
        Assert.AreEqual(0, startedCount);
    }

    [TestMethod]
    public async Task ApprovedCancellationStopsWaitingAndPreventsAnotherChild()
    {
        var setup = await ArrangeAsync(async (context, token) =>
        {
            var machine = (await context.GetApprovedMachinesAsync(token))[0];
            try { await context.CallMachineAsync(MachineWorkflows.AgentStatus, machine, new EmptyInput(), token); }
            finally
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => context.CallMachineAsync(MachineWorkflows.AgentStatus, machine, new EmptyInput(), CancellationToken.None));
            }
            return [];
        });
        var child = await NextChildAsync(setup.Parent.Id, 1);
        await DatabaseAsync(async db =>
        {
            var box = new LockboxRequest { Decision = DecisionState.Approved,
                Parameters = JsonSerializer.Serialize(new OperationRequest { TargetWorkflowId = setup.Parent.Id }) };
            var cancellation = new WorkflowRun { LockboxId = box.Id, Definition = WorkflowNames.Kill };
            await new CancelWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, cancellation, WorkflowLaunch.FromApproval(box, cancellation.Id)));
            await db.SaveChangesAsync(); return true;
        });
        var run = await WaitAsync(setup.Parent.Id, r => r.State == RunState.Cancelled);
        Assert.AreEqual(RunState.Cancelled, (await WaitAsync(child.Id, _ => true)).State);
        Assert.AreEqual(1, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(r => r.ParentId == run.Id)));
    }

}
