using Aiursoft.DMS.Authorization;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class ClusterDiskUsageTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(Guid Cluster, string User, string Approver, Guid[] Machines, string[] Keys, string Key)> ArrangeAsync()
    {
        return await DatabaseAsync(async db =>
        {
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
            var approver = new User { Id = Guid.NewGuid().ToString(), UserName = "disk-reviewer", DisplayName = "Disk reviewer" };
            var cluster = new Cluster { Name = "Disk usage" };
            var keys = Enumerable.Range(0, 2).Select(_ => DmsKeyAuthenticationHandler.CreateKey(true)).ToArray();
            var machines = keys.Select((key, i) => new Machine { ClusterId = cluster.Id, Name = $"disk-{i}", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }).ToArray();
            var personal = DmsKeyAuthenticationHandler.CreateKey(false);
            db.Users.Add(approver); db.Clusters.Add(cluster); db.Machines.AddRange(machines);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Disk usage", KeyHash = DmsKeyAuthenticationHandler.Hash(personal), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: false, approve: false);
            DmsTestPermissions.Risk(db, approver.Id, RiskLevel.Safe, request: false, automatic: false, approve: true);
            await db.SaveChangesAsync();
            return (cluster.Id, user.Id, approver.Id, machines.Select(m => m.Id).ToArray(), keys, personal);
        });
    }

    private async Task<WorkflowRun> WaitAsync(Guid id, Func<WorkflowRun, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var run = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == id));
            if (predicate(run)) return run;
            await Task.Delay(60, timeout.Token);
        }
    }

    private async Task<WorkflowRun> ChildAsync(Guid parent, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var children = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.ParentId == parent).OrderBy(r => r.CreatedAt).ToListAsync());
            if (children.Count >= count) return children[count - 1];
            await Task.Delay(60, timeout.Token);
        }
    }

    private Task CompleteAsync(WorkflowRun run, RunState state, object result) => DatabaseAsync(async db =>
    {
        var child = await db.WorkflowRuns.SingleAsync(r => r.Id == run.Id);
        child.State = state; child.Result = JsonSerializer.Serialize(result);
        await db.SaveChangesAsync(); return true;
    });

    private async Task<LockboxRequest> SubmitAsync(Guid cluster, string user, string approver)
    {
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var box = await service.SubmitAsync(user, ClusterWorkflows.DiskUsage.Request(cluster, new EmptyInput(), "Disk usage acceptance", allMachines: true));
        Assert.AreEqual(RiskLevel.Safe, box.Risk);
        return await service.DecideAsync(approver, box.Id, true, "Read the frozen machines");
    }

    [TestMethod]
    public async Task ClusterCommandUsesApprovedSnapshotAndTwoActualAgents()
    {
        var setup = await ArrangeAsync();
        var cli = await Client.DmsCommandApp.Create().TestRunAsync(["cluster", "get", "disk-usage", "--cluster-id", setup.Cluster.ToString(), "--all-machines", "--reason", "Real disk query", "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        var box = await DatabaseAsync(db => db.Lockboxes.AsNoTracking().SingleAsync(l => l.ClusterId == setup.Cluster));
        Assert.AreEqual(DecisionState.Pending, box.Decision); Assert.IsNull(box.OperationId);
        Assert.AreEqual(RiskLevel.Safe, box.Risk); Assert.AreEqual(ClusterWorkflows.DiskUsage.Version, box.Version);
        await DatabaseAsync(async db => { db.Machines.Add(new Machine { ClusterId = setup.Cluster, Name = "Late disk machine", KeyHash = "late" }); await db.SaveChangesAsync(); return true; });
        using (var scope = Server!.Services.CreateScope())
            box = await scope.ServiceProvider.GetRequiredService<WorkflowService>().DecideAsync(setup.Approver, box.Id, true, "Only original two machines");
        var paths = setup.Machines.Select(_ => Directory.CreateTempSubdirectory("dms-disk-agent-").FullName).ToArray();
        var journals = paths.Select(path => new TaskJournal(path)).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var agents = setup.Keys.Select((key, i) => new AgentClient(Http.BaseAddress!, key, journals[i]).RunAsync(stop.Token)).ToArray();
        try
        {
            var parent = await WaitAsync(box.OperationId!.Value, r => r.State.Terminal());
            Assert.AreEqual(RunState.Succeeded, parent.State, parent.Result);
            var result = ClusterWorkflows.DiskUsage.ReadResult(parent.Result);
            Assert.AreEqual(2, result.MachineCount); Assert.HasCount(2, result.Machines);
            Assert.IsGreaterThan(0L, result.TotalBytes);
            Assert.AreEqual(result.Machines.Sum(m => m.Usage.TotalBytes), result.TotalBytes);
            Assert.AreEqual(result.Machines.Sum(m => m.Usage.UsedBytes), result.UsedBytes);
            Assert.AreEqual(result.Machines.Sum(m => m.Usage.AvailableBytes), result.AvailableBytes);
            CollectionAssert.AreEquivalent(setup.Machines, result.Machines.Select(m => m.MachineId).ToArray());
            var tree = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == box.WorkflowId).ToListAsync());
            Assert.HasCount(6, tree); Assert.AreEqual(tree.Count, tree.Select(r => r.Id).Distinct().Count());
            Assert.IsTrue(tree.All(r => r.Id != Guid.Empty && r.RootId == box.WorkflowId));
            foreach (var machine in result.Machines)
            {
                var child = tree.Single(r => r.Id == machine.Usage.WorkflowId);
                Assert.AreEqual(machine.MachineId, child.MachineId); Assert.AreEqual(parent.Id, child.ParentId);
                Assert.AreEqual(MachineWorkflows.DiskUsage.Name, child.Definition);
                Assert.AreEqual(MachineWorkflows.DiskUsage.Version, child.Version);
                Assert.IsGreaterThan(0, machine.Usage.Filesystems);
                var process = tree.Single(r => r.ParentId == child.Id);
                Assert.AreEqual(WorkflowNames.Process, process.Definition); Assert.AreEqual(RunState.Succeeded, process.State);
            }
            foreach (var format in new[] { "table", "json", "csv" })
            {
                var details = await Client.DmsCommandApp.Create().TestRunAsync(["operation", "get-workflow", "--id", parent.Id.ToString(), "--server", Http.BaseAddress!.ToString(), "--api-key", setup.Key, "--output-format", format]);
                Assert.AreEqual(0, details.ProgramReturn, details.StdErr);
                StringAssert.Contains(details.StdOut, "TotalBytes"); StringAssert.Contains(details.StdOut, "AvailableBytes");
                foreach (var row in result.Machines) StringAssert.Contains(details.StdOut, row.Usage.WorkflowId.ToString());
            }
        }
        finally
        {
            await stop.CancelAsync(); try { await Task.WhenAll(agents); } catch (OperationCanceledException) { }
            foreach (var journal in journals) journal.Dispose();
            foreach (var path in paths) Directory.Delete(path, true);
        }
    }

    [TestMethod]
    [DataRow(RunState.Failed)]
    [DataRow(RunState.NeedsVerification)]
    public async Task AnIncompleteMachineDoesNotPublishSuccessfulPartialTotals(RunState state)
    {
        var setup = await ArrangeAsync(); var box = await SubmitAsync(setup.Cluster, setup.User, setup.Approver);
        var first = await ChildAsync(box.OperationId!.Value, 1);
        await CompleteAsync(first, RunState.Succeeded, new DiskUsageResult(first.Id, 100, 20, 70, 1));
        var second = await ChildAsync(box.OperationId.Value, 2);
        await CompleteAsync(second, state, new { Error = "Injected incomplete machine outcome" });
        var parent = await WaitAsync(box.OperationId.Value, r => r.State == state);
        Assert.DoesNotContain("TotalBytes", parent.Result);
        Assert.AreEqual(RunState.Succeeded, (await WaitAsync(first.Id, _ => true)).State);
        Assert.AreEqual(state, (await WaitAsync(second.Id, _ => true)).State);
        await Task.Delay(1200);
        Assert.AreEqual(state, (await WaitAsync(parent.Id, _ => true)).State);
    }

    [TestMethod]
    public async Task ClusterSumCannotWrapLargeByteCountsIntoSuccess()
    {
        var setup = await ArrangeAsync(); var box = await SubmitAsync(setup.Cluster, setup.User, setup.Approver);
        var first = await ChildAsync(box.OperationId!.Value, 1);
        await CompleteAsync(first, RunState.Succeeded, new DiskUsageResult(first.Id, long.MaxValue, 0, 0, 1));
        var second = await ChildAsync(box.OperationId.Value, 2);
        await CompleteAsync(second, RunState.Succeeded, new DiskUsageResult(second.Id, 1, 0, 0, 1));
        var parent = await WaitAsync(box.OperationId.Value, r => r.State.Terminal());
        Assert.AreEqual(RunState.Failed, parent.State); Assert.DoesNotContain("TotalBytes", parent.Result);
    }

    [TestMethod]
    public async Task DiskUsageRequiresSafePermissionAndTargetsWhileMachineHelperStaysInternal()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Safe), false); await db.SaveChangesAsync();
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            ClusterWorkflows.DiskUsage.Request(setup.Cluster, new EmptyInput(), "No Safe grant", allMachines: true)))).StatusCode);
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Safe)); await db.SaveChangesAsync();
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            ClusterWorkflows.DiskUsage.Request(setup.Cluster, new EmptyInput(), "No targets")))).StatusCode);
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            MachineWorkflows.DiskUsage.Request(setup.Cluster, new EmptyInput(), "Internal helper", setup.Machines)))).StatusCode);
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.ClusterId == setup.Cluster));
    }
}
