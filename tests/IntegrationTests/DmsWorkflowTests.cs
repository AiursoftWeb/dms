using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class DmsWorkflowTests : TestBase
{
    private async Task<(Guid Cluster, string User, string Approver, Guid Machine)> ArrangeAsync(bool automatic = false)
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == "admin");
        var approver = new User { DisplayName = "Approver", UserName = "approver", Id = Guid.NewGuid().ToString() };
        db.Users.Add(approver);
        var cluster = new Cluster { Name = "Test" }; db.Clusters.Add(cluster);
        var machine = new Machine { ClusterId = cluster.Id, Name = "Test machine", KeyHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)) };
        db.Machines.Add(machine);
        foreach (var risk in new[] { RiskLevel.Safe, RiskLevel.Low, RiskLevel.Medium })
        {
            db.ClusterGrants.Add(new ClusterGrant { ClusterId = cluster.Id, UserId = user.Id, Risk = risk, CanRequest = true, AutoApprove = automatic, CanApprove = true });
            db.ClusterGrants.Add(new ClusterGrant { ClusterId = cluster.Id, UserId = approver.Id, Risk = risk, CanApprove = true });
        }
        await db.SaveChangesAsync();
        return (cluster.Id, user.Id, approver.Id, machine.Id);
    }

    private OperationRequest Request(Guid cluster, Guid machine) => new()
    {
        ClusterId = cluster, Workflow = WorkflowNames.Restart, MachineIds = [machine], Service = "dms-test.service", Reason = "Business acceptance"
    };

    [TestMethod]
    public async Task ApprovalRequiresAnotherUserAndCreatesOneChild()
    {
        var setup = await ArrangeAsync();
        Guid id;
        using (var scope = Server!.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var item = await service.SubmitAsync(setup.User, Request(setup.Cluster, setup.Machine));
            Assert.AreEqual(DecisionState.Pending, item.Decision); Assert.IsNull(item.OperationId); id = item.Id;
            var forbidden = await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.User, id, true, "Self approval"));
            Assert.AreEqual(403, forbidden.StatusCode);
        }
        using (var scope = Server!.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var item = await service.DecideAsync(setup.Approver, id, true, "Approved maintenance");
            Assert.AreEqual(DecisionState.Approved, item.Decision); Assert.IsNotNull(item.OperationId);
            var tree = await service.CallstackAsync(setup.User, item.WorkflowId);
            Assert.IsTrue(tree.Any(r => r.Id == item.OperationId && r.ParentId == item.WorkflowId));
            var conflict = await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.Approver, id, false, "Late rejection"));
            Assert.AreEqual(409, conflict.StatusCode);
        }
    }

    [TestMethod]
    public async Task AutoApprovalDoesNotNeedSelfApprovalAndTargetsStayFrozen()
    {
        var setup = await ArrangeAsync(true);
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = Request(setup.Cluster, setup.Machine); request.AllMachines = true; request.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, request);
        Assert.IsTrue(item.Automatic); Assert.AreEqual(DecisionState.Approved, item.Decision); Assert.IsNull(item.ApproverId);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        db.Machines.Add(new Machine { ClusterId = setup.Cluster, Name = "Added after submission", KeyHash = "another" }); await db.SaveChangesAsync();
        Assert.AreEqual(item.Id, (await service.SubmitAsync(setup.User, request)).Id);
        var targets = JsonSerializer.Deserialize<List<Guid>>(item.TargetMachines)!;
        CollectionAssert.AreEqual(new[] { setup.Machine }, targets);
        request.Service = "changed.service";
        var conflict = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, request));
        Assert.AreEqual(409, conflict.StatusCode);
    }

    [TestMethod]
    public async Task DenialStartsNoOperationAndExplicitSelfApprovalWorks()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var item = await service.SubmitAsync(setup.User, Request(setup.Cluster, setup.Machine));
        await service.DecideAsync(setup.Approver, item.Id, false, "Maintenance window closed");
        Assert.IsNull(item.OperationId);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == setup.User); user.CanApproveOwnRequests = true; await db.SaveChangesAsync();
        var second = await service.SubmitAsync(setup.User, Request(setup.Cluster, setup.Machine));
        await service.DecideAsync(setup.User, second.Id, true, "Explicit self approval grant");
        Assert.AreEqual(DecisionState.Approved, second.Decision);
    }

    [TestMethod]
    public async Task CrossClusterAndInternalWorkflowRequestsAreRejected()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var other = new Cluster { Name = "Other" }; db.Clusters.Add(other); await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var denied = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, Request(other.Id, setup.Machine)));
        Assert.AreEqual(403, denied.StatusCode);
        var request = Request(setup.Cluster, setup.Machine); request.Workflow = WorkflowNames.Process;
        var invalid = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, request)); Assert.AreEqual(400, invalid.StatusCode);
    }

    [TestMethod]
    public async Task AgentKeyCannotInvokeHumanApiAndPersonalKeyCanSubmit()
    {
        var setup = await ArrangeAsync(true);
        var agentKey = DmsKeyAuthenticationHandler.CreateKey(true);
        var personal = DmsKeyAuthenticationHandler.CreateKey(false);
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(agentKey);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = setup.User, KeyHash = DmsKeyAuthenticationHandler.Hash(personal), Name = "test", ExpiresAt = DateTime.UtcNow.AddHours(1) });
            await db.SaveChangesAsync();
        }
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", agentKey);
        var denied = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", Request(setup.Cluster, setup.Machine));
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personal);
        var accepted = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", Request(setup.Cluster, setup.Machine));
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        var websocket = await Http.GetAsync("/api/agent/channel"); Assert.AreEqual(HttpStatusCode.Forbidden, websocket.StatusCode);
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([
            "machine", "performance", "info", "--server", Http.BaseAddress!.ToString(), "--api-key", personal,
            "--cluster-id", setup.Cluster.ToString(), "--machine-id", setup.Machine.ToString(), "--reason", "CLI acceptance", "--json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        StringAssert.Contains(cli.StdOut, "WorkflowId");
        foreach (var command in new[] { new[] { "agent", "status" }, new[] { "systemctl", "status" }, new[] { "layout", "lsblk" }, new[] { "layout", "df" } })
        {
            var args = new List<string> { "machine", command[0], command[1], "--server", Http.BaseAddress!.ToString(), "--api-key", personal,
                "--cluster-id", setup.Cluster.ToString(), "--machine-id", setup.Machine.ToString(), "--reason", "Read only CLI", "--json" };
            if (command[0] == "systemctl") args.AddRange(["--service", "dms-test.service"]);
            var result = await Client.DmsCommandApp.Create().TestRunAsync(args.ToArray());
            Assert.AreEqual(0, result.ProgramReturn, result.StdErr);
            StringAssert.Contains(result.StdOut, "WorkflowId");
        }
        var invalidRequest = Request(setup.Cluster, setup.Machine);
        invalidRequest.Service = "*.service";
        var invalid = await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", invalidRequest);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        var error = await invalid.Content.ReadFromJsonAsync<Aiursoft.AiurProtocol.Models.AiurCollection<string>>();
        Assert.IsNotNull(error?.Items);
        Assert.HasCount(1, error.Items);
        var empty = await Http.PostAsJsonAsync<OperationRequest?>("/api/v1/dms/lockboxes", null);
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [TestMethod]
    public async Task ConcurrentApprovalsCreateOnlyOneOperation()
    {
        var setup = await ArrangeAsync();
        Guid id;
        using (var scope = Server!.Services.CreateScope())
            id = (await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, Request(setup.Cluster, setup.Machine))).Id;
        async Task<bool> Decide()
        {
            using var scope = Server.Services.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<WorkflowService>().DecideAsync(setup.Approver, id, true, "Concurrent review");
                return true;
            }
            catch (DmsException exception) when (exception.StatusCode == 409) { return false; }
        }
        var results = await Task.WhenAll(Decide(), Decide());
        Assert.AreEqual(1, results.Count(success => success));
        using var check = Server.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var item = await db.Lockboxes.SingleAsync(l => l.Id == id);
        Assert.AreEqual(1, await db.WorkflowRuns.CountAsync(r => r.ParentId == item.WorkflowId));
    }

    [TestMethod]
    public async Task ManualApprovalDispatchesExactlyTheTenSubmittedMachines()
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        for (var i = 1; i < 10; i++) db.Machines.Add(new Machine { ClusterId = setup.Cluster, Name = $"Machine {i}", KeyHash = Guid.NewGuid().ToString() });
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = Request(setup.Cluster, setup.Machine); request.AllMachines = true; request.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, request);
        var late = new Machine { ClusterId = setup.Cluster, Name = "Eleventh machine", KeyHash = Guid.NewGuid().ToString() };
        db.Machines.Add(late); await db.SaveChangesAsync();
        await service.DecideAsync(setup.Approver, item.Id, true, "Ten machines approved");
        var operation = await db.WorkflowRuns.SingleAsync(r => r.Id == item.OperationId);
        await new MachineBatchWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, operation, item));
        var dispatched = db.WorkflowRuns.Local.Where(r => r.ParentId == operation.Id).ToList();
        Assert.HasCount(10, dispatched);
        Assert.IsFalse(dispatched.Any(r => r.MachineId == late.Id));
    }

    [TestMethod]
    public async Task CorePagesRenderWithAuthorizedUser()
    {
        await ArrangeAsync(); await LoginAsAdmin();
        foreach (var route in new[] { "/Dms", "/Dms/Lockboxes", "/Dms/Operations", "/DmsAccess", "/DmsAccess/Keys" })
        {
            var response = await Http.GetAsync(route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, route);
        }
    }
    [TestMethod]
    public async Task LegacyAgentRejectsNewDefinitionsButKeepsExistingWorkflowsAndOfflineQueue()
    {
        var setup = await ArrangeAsync(true);
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        var requests = new List<LockboxRequest>();
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(key);
            await db.SaveChangesAsync();
            foreach (var definition in WorkflowNames.MachineDefinitions)
            {
                var input = Request(setup.Cluster, setup.Machine); input.Workflow = definition;
                requests.Add(await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, input));
            }
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<List<WorkflowRun>> Tasks(CancellationToken token)
        {
            using var scope = Server!.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<TemplateDbContext>().WorkflowRuns.AsNoTracking()
                .Where(r => r.MachineId == setup.Machine && r.IsMachineTask).ToListAsync(token);
        }
        List<WorkflowRun> tasks;
        do { await Task.Delay(100, timeout.Token); tasks = await Tasks(timeout.Token); } while (tasks.Count < requests.Count);
        Assert.IsTrue(tasks.All(t => t.State == RunState.Queued));
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        var uri = new UriBuilder(Http.BaseAddress!) { Scheme = "ws", Path = "/api/agent/channel" }.Uri;
        await socket.ConnectAsync(uri, timeout.Token);
        // The original Agent sends no capability field.
        await WebSocketJson.SendAsync(socket, new AgentMessage { Kind = "hello", InstallationId = Guid.NewGuid(), Version = "1.0.0" }, timeout.Token);
        do { await Task.Delay(100, timeout.Token); tasks = await Tasks(timeout.Token); }
        while (tasks.Count(t => t.State == RunState.Failed) < 4);
        Assert.IsTrue(tasks.Where(t => t.Definition is WorkflowNames.Performance or WorkflowNames.Restart).All(t => t.State == RunState.Offered));
        foreach (var failed in tasks.Where(t => t.State == RunState.Failed))
        {
            StringAssert.Contains(failed.Result, "does not support");
            Assert.IsNull(failed.StartedAt); Assert.IsNotNull(failed.FinishedAt);
        }
        Assert.AreEqual(WebSocketState.Open, socket.State);
    }

    [TestMethod]
    [DataRow(WorkflowNames.AgentStatus, RiskLevel.Safe)]
    [DataRow(WorkflowNames.ServiceStatus, RiskLevel.Low)]
    [DataRow(WorkflowNames.ListBlockDevices, RiskLevel.Safe)]
    [DataRow(WorkflowNames.DiskFree, RiskLevel.Safe)]
    public async Task ReadOnlyCommandsUseRiskGrantsAndApprovedFrozenBatch(string definition, RiskLevel risk)
    {
        var setup = await ArrangeAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var grant = await db.ClusterGrants.SingleAsync(g => g.ClusterId == setup.Cluster && g.UserId == setup.User && g.Risk == risk);
        grant.CanRequest = false; await db.SaveChangesAsync();
        var input = Request(setup.Cluster, setup.Machine); input.Workflow = definition;
        var denied = await Assert.ThrowsAsync<DmsException>(() => service.SubmitAsync(setup.User, input));
        Assert.AreEqual(403, denied.StatusCode);
        grant.CanRequest = true; await db.SaveChangesAsync();
        input.AllMachines = true; input.MachineIds.Clear();
        var item = await service.SubmitAsync(setup.User, input);
        Assert.AreEqual(risk, item.Risk); Assert.AreEqual(DecisionState.Pending, item.Decision);
        Assert.IsNull(item.OperationId);
        db.Machines.Add(new Machine { ClusterId = setup.Cluster, Name = "Late target", KeyHash = "late" });
        await db.SaveChangesAsync();
        await service.DecideAsync(setup.Approver, item.Id, true, "Read only approval");
        var operation = await db.WorkflowRuns.SingleAsync(r => r.Id == item.OperationId);
        Assert.AreEqual(WorkflowNames.Batch, operation.Definition);
        await new MachineBatchWorkflow().ExecuteAsync(new ClusterWorkflowContext(db, operation, item));
        var child = db.WorkflowRuns.Local.Single(r => r.ParentId == operation.Id);
        Assert.AreEqual(setup.Machine, child.MachineId); Assert.AreEqual(definition, child.Definition);
    }

    [TestMethod]
    [DataRow(WorkflowNames.Performance, "Processors")]
    [DataRow(WorkflowNames.AgentStatus, "InstallationId")]
    [DataRow(WorkflowNames.ListBlockDevices, "blockdevices")]
    [DataRow(WorkflowNames.DiskFree, "Filesystem")]
    public async Task ActualAgentExecutesApprovedWorkflowAndReturnsTrace(string definition, string expected)
    {
        var setup = await ArrangeAsync(true);
        var key = DmsKeyAuthenticationHandler.CreateKey(true);
        LockboxRequest request;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            (await db.Machines.SingleAsync(m => m.Id == setup.Machine)).KeyHash = DmsKeyAuthenticationHandler.Hash(key);
            await db.SaveChangesAsync();
            var input = Request(setup.Cluster, setup.Machine); input.Workflow = definition;
            request = await scope.ServiceProvider.GetRequiredService<WorkflowService>().SubmitAsync(setup.User, input);
        }
        var path = Path.Combine(Path.GetTempPath(), "dms-real-agent-" + Guid.NewGuid());
        using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using var journal = new Agent.TaskJournal(path);
            var agent = new Agent.AgentClient(Http.BaseAddress!, key, journal).RunAsync(stopping.Token);
            try
            {
                WorkflowRun? operation = null;
                while (!stopping.IsCancellationRequested)
                {
                    using var scope = Server!.Services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
                    operation = await db.WorkflowRuns.AsNoTracking().SingleAsync(r => r.Id == request.OperationId);
                    if (operation.State.Terminal()) break;
                    await Task.Delay(100, stopping.Token);
                }
                Assert.IsNotNull(operation); Assert.AreEqual(RunState.Succeeded, operation.State);
                using var check = Server!.Services.CreateScope();
                var tree = await check.ServiceProvider.GetRequiredService<WorkflowService>().CallstackAsync(setup.User, request.WorkflowId);
                var machine = tree.Single(r => r.IsMachineTask);
                Assert.AreEqual(RunState.Succeeded, machine.State);
                StringAssert.Contains(machine.Result, expected);
                Assert.IsTrue(machine.LastSequence >= 3);
                Assert.IsTrue(Server.Services.GetRequiredService<TraceStore>().PendingCount > 0);
            }
            finally
            {
                await stopping.CancelAsync();
                try { await agent; } catch (OperationCanceledException) { }
            }
        }
        finally { Directory.Delete(path, true); }
    }

}
