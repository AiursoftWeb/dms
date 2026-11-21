using System.Net.WebSockets;
using System.Text.Json;
using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Authorization;
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

    private async Task<(string User, Guid[] Machines, string[] Keys, string Key)> ArrangeAsync()
    {
        return await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" };
            var keys = Enumerable.Range(0, 3).Select(_ => DmsKeyAuthenticationHandler.CreateKey(true)).ToArray();
            var machines = keys.Select((key, i) => new Machine { Name = $"disk-{i}", KeyHash = DmsKeyAuthenticationHandler.Hash(key) }).ToArray();
            var personal = DmsKeyAuthenticationHandler.CreateKey(false);
            db.Users.Add(user);
            db.Machines.AddRange(machines);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = user.Id, Name = "Machine overview", KeyHash = DmsKeyAuthenticationHandler.Hash(personal), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Open, request: true, automatic: false, approve: false);
            await db.SaveChangesAsync();
            return (user.Id, machines.Select(m => m.Id).ToArray(), keys, personal);
        });
    }

    private async Task WaitForReadyAsync(IEnumerable<Guid> ids, CancellationToken token)
    {
        var sessions = Server!.Services.GetRequiredService<AgentSessions>();
        while (ids.Any(id => sessions.Get(id)?.Ready != true)) await Task.Delay(30, token);
    }

    private async Task<WorkflowRun> WaitAsync(Guid id, Func<WorkflowRun, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var run = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == id, timeout.Token));
            if (predicate(run)) return run;
            await Task.Delay(60, timeout.Token);
        }
    }

    private async Task<Guid> SubmitAsync(string user)
    {
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var box = await service.SubmitAsync(user, ClusterWorkflows.DiskUsage.Request(new EmptyInput(), "", allMachines: true));
        Assert.AreEqual(RiskLevel.Open, box.Risk);
        Assert.AreEqual(DecisionState.Approved, box.Decision);
        Assert.IsTrue(box.Automatic);
        return box.WorkflowId!.Value;
    }

    private async Task<(int ProgramReturn, string StdOut, string StdErr)> CliAsync(string key, params string[] command)
    {
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([..command, "--server", Http.BaseAddress!.ToString(), "--api-key", key]);
        return (cli.ProgramReturn, cli.StdOut, cli.StdErr);
    }

    [TestMethod]
    public async Task CommandsPrintDirectResultsFromRealAgentsAndKeepOfflineRows()
    {
        var setup = await ArrangeAsync();
        var paths = setup.Machines.Take(2).Select(_ => Directory.CreateTempSubdirectory("dms-probe-agent-").FullName).ToArray();
        var journals = paths.Select(path => new TaskJournal(path)).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var agents = journals.Select((journal, i) => new AgentClient(Http.BaseAddress!, setup.Keys[i], journal).RunAsync(stop.Token)).ToArray();
        try
        {
            await WaitForReadyAsync(setup.Machines.Take(2), stop.Token);
            var cli = await CliAsync(setup.Key, "machine", "disk-usage", "--all-machines", "--output-format", "json");
            Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
            var result = ClusterWorkflows.DiskUsage.ReadResult(cli.StdOut);
            Assert.AreEqual(3, result.TargetMachines);
            Assert.AreEqual(2, result.MeasuredMachines);
            Assert.HasCount(3, result.Machines);
            Assert.IsGreaterThan(0L, result.TotalBytes.GetValueOrDefault());
            Assert.AreEqual(result.Machines.Sum(m => m.TotalBytes), result.TotalBytes);
            Assert.AreEqual(result.Machines.Sum(m => m.UsedBytes), result.UsedBytes);
            Assert.AreEqual(result.Machines.Sum(m => m.AvailableBytes), result.AvailableBytes);
            Assert.AreEqual("Offline", result.Machines[^1].Status);
            Assert.AreEqual(setup.Machines[2], result.Machines[^1].MachineId);
            Assert.IsNull(result.Machines[^1].TotalBytes);
            Assert.IsNull(result.Machines[^1].WorkflowId);
            var box = await DatabaseAsync(db => db.Lockboxes.AsNoTracking().SingleAsync(l => l.RequesterId == setup.User));
            Assert.AreEqual(RiskLevel.Open, box.Risk);
            Assert.IsTrue(box.Automatic);
            var tree = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking().Where(r => r.RootId == box.WorkflowId).ToListAsync());
            Assert.HasCount(5, tree);
            Assert.AreEqual(RunState.Warning, tree.Single(r => r.Id == box.WorkflowId).State);
            foreach (var row in result.Machines.Take(2))
            {
                var child = tree.Single(r => r.Id == row.WorkflowId);
                Assert.AreEqual(row.MachineId, child.MachineId);
                Assert.AreEqual(box.WorkflowId, child.ParentId);
                Assert.AreEqual(MachineWorkflows.DiskUsage.Name, child.Definition);
                Assert.AreEqual(WorkflowNames.Process, tree.Single(r => r.ParentId == child.Id).Definition);
            }

            var health = await CliAsync(setup.Key, "machine", "agent", "status", "--all-machines", "--output-format", "json");
            Assert.AreEqual(0, health.ProgramReturn, health.StdErr);
            var rows = ClusterWorkflows.AgentHealth.ReadResult(health.StdOut);
            Assert.HasCount(3, rows);
            Assert.IsTrue(rows.Take(2).All(row => row.Online && row.Status == "Succeeded" &&
                !string.IsNullOrEmpty(row.Version) && row.ActiveTasks >= 0 && row.PendingEvents >= 0));
            Assert.IsFalse(rows[2].Online);
            Assert.AreEqual("Offline", rows[2].Status);
            Assert.IsNull(rows[2].PendingEvents);

            foreach (var format in new[] { "table", "csv" })
            {
                var disk = await CliAsync(setup.Key, "machine", "disk-usage", "--all-machines", "--output-format", format);
                Assert.AreEqual(0, disk.ProgramReturn, disk.StdErr);
                StringAssert.Contains(disk.StdOut, "disk-2");
                StringAssert.Contains(disk.StdOut, format == "table" ? "Totals for 2 of 3" : "TotalBytes");
                var agent = await CliAsync(setup.Key, "machine", "agent", "status", "--all-machines", "--output-format", format);
                Assert.AreEqual(0, agent.ProgramReturn, agent.StdErr);
                StringAssert.Contains(agent.StdOut, "Offline");
                StringAssert.Contains(agent.StdOut, format == "table" ? "Pending events" : "PendingEvents");
            }
        }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(agents); } catch (OperationCanceledException) { }
            foreach (var journal in journals) journal.Dispose();
            foreach (var path in paths) Directory.Delete(path, true);
        }
    }

    private async Task<ClientWebSocket> ConnectIdleAgentAsync(Guid machine, string key, CancellationToken token)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
            await socket.ConnectAsync(new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/api/agent/channel" }.Uri, token);
            await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "hello", Version = "fixture", InstallationId = Guid.NewGuid() }, token);
            await WaitForReadyAsync([machine], token);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    [TestMethod]
    [DataRow(RunState.Failed, "Failed")]
    [DataRow(RunState.NeedsVerification, "NeedsVerification")]
    [DataRow(null, "TimedOut")]
    public async Task UnmeasuredRowsNeverEnterDiskTotals(RunState? state, string status)
    {
        var setup = await ArrangeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = await ConnectIdleAgentAsync(setup.Machines[0], setup.Keys[0], timeout.Token);
        using var second = await ConnectIdleAgentAsync(setup.Machines[1], setup.Keys[1], timeout.Token);
        var root = await SubmitAsync(setup.User);
        var firstOffer = await WebSocketJson.ReceiveAsync(first, timeout.Token);
        var secondOffer = await WebSocketJson.ReceiveAsync(second, timeout.Token);
        Assert.AreEqual("offer", firstOffer?.Kind);
        Assert.AreEqual("offer", secondOffer?.Kind);
        // Add a machine after submission to exercise the frozen target snapshot.
        await DatabaseAsync(async db =>
        {
            db.Machines.Add(new Machine { Name = "Late machine", KeyHash = "fixture" });
            var good = await db.WorkflowRuns.SingleAsync(r => r.Id == firstOffer!.TaskId);
            good.State = RunState.Succeeded;
            good.Result = JsonSerializer.Serialize(new DiskUsageResult(good.Id, 100, 82, 16, 1));
            if (state is { } outcome)
                (await db.WorkflowRuns.SingleAsync(r => r.Id == secondOffer!.TaskId)).State = outcome;
            await db.SaveChangesAsync();
            return true;
        });
        var parent = await WaitAsync(root, run => run.State.Terminal());
        Assert.AreEqual(RunState.Warning, parent.State, parent.Result);
        var result = ClusterWorkflows.DiskUsage.ReadResult(parent.Result);
        Assert.AreEqual(3, result.TargetMachines);
        Assert.AreEqual(1, result.MeasuredMachines);
        Assert.AreEqual(100L, result.TotalBytes);
        Assert.AreEqual(82m, result.Machines[0].UsedPercent);
        Assert.AreEqual(status, result.Machines.Single(row => row.MachineId == setup.Machines[1]).Status);
        Assert.IsNull(result.Machines.Single(row => row.MachineId == setup.Machines[1]).TotalBytes);
        var incomplete = await WaitAsync(secondOffer!.TaskId, _ => true);
        Assert.AreEqual(state ?? RunState.Cancelled, incomplete.State);
    }

    [TestMethod]
    public async Task AllOfflineProducesNoCapacityAndCreatesNoMachineWork()
    {
        var setup = await ArrangeAsync();
        var root = await SubmitAsync(setup.User);
        var run = await WaitAsync(root, row => row.State.Terminal());
        Assert.AreEqual(RunState.Warning, run.State);
        var result = ClusterWorkflows.DiskUsage.ReadResult(run.Result);
        Assert.AreEqual(0, result.MeasuredMachines);
        Assert.IsNull(result.TotalBytes);
        Assert.HasCount(3, result.Machines);
        Assert.IsTrue(result.Machines.All(row => row.Status == "Offline" && row.WorkflowId == null));
        Assert.AreEqual(0, await DatabaseAsync(db => db.WorkflowRuns.CountAsync(row => row.ParentId == root)));
    }

    [TestMethod]
    public async Task ClusterSumCannotWrapLargeByteCountsIntoSuccess()
    {
        var setup = await ArrangeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = await ConnectIdleAgentAsync(setup.Machines[0], setup.Keys[0], timeout.Token);
        using var second = await ConnectIdleAgentAsync(setup.Machines[1], setup.Keys[1], timeout.Token);
        var root = await SubmitAsync(setup.User);
        var firstOffer = await WebSocketJson.ReceiveAsync(first, timeout.Token);
        var secondOffer = await WebSocketJson.ReceiveAsync(second, timeout.Token);
        await DatabaseAsync(async db =>
        {
            var good = await db.WorkflowRuns.SingleAsync(r => r.Id == firstOffer!.TaskId);
            var other = await db.WorkflowRuns.SingleAsync(r => r.Id == secondOffer!.TaskId);
            good.State = other.State = RunState.Succeeded;
            good.Result = JsonSerializer.Serialize(new DiskUsageResult(good.Id, long.MaxValue, 0, 0, 1));
            other.Result = JsonSerializer.Serialize(new DiskUsageResult(other.Id, 1, 0, 0, 1));
            await db.SaveChangesAsync();
            return true;
        });
        var parent = await WaitAsync(root, r => r.State.Terminal());
        Assert.AreEqual(RunState.Failed, parent.State);
        Assert.DoesNotContain("TotalBytes", parent.Result);
    }

    [TestMethod]
    public async Task OverviewsRequireOpenPermissionAndTargetsWhileDiskProbeStaysInternal()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Open), false);
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Safe));
        await db.SaveChangesAsync();
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            ClusterWorkflows.DiskUsage.Request(new EmptyInput(), "", allMachines: true)))).StatusCode);
        Assert.AreEqual(403, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            ClusterWorkflows.AgentHealth.Request(new EmptyInput(), "", allMachines: true)))).StatusCode);
        DmsTestPermissions.Set(db, setup.User, DmsPermissions.Request(RiskLevel.Open));
        await db.SaveChangesAsync();
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            ClusterWorkflows.DiskUsage.Request(new EmptyInput(), "")))).StatusCode);
        Assert.AreEqual(400, (await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User,
            MachineWorkflows.DiskUsage.Request(new EmptyInput(), "Internal helper", setup.Machines)))).StatusCode);
        Assert.AreEqual(0, await db.Lockboxes.CountAsync(l => l.RequesterId == setup.User));
    }
}
