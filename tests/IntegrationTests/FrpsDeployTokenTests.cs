using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public sealed class FrpsDeployTokenTests : TestBase
{
    [TestMethod]
    public async Task ApprovalStoresNoPlaintextAndOnlyTheRunningTargetCanReadTheToken()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var workflows = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var requester = new User { UserName = "frps-requester", DisplayName = "FRPS requester" };
        var approver = new User { UserName = "frps-approver", DisplayName = "FRPS approver" };
        db.Users.AddRange(requester, approver);
        DmsTestPermissions.Risk(db, requester.Id, RiskLevel.VeryHigh, request: true);
        DmsTestPermissions.Risk(db, approver.Id, RiskLevel.VeryHigh, approve: true);
        var agent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var otherAgent = DmsKeyAuthenticationHandler.CreateKey(machine: true);
        var target = new Machine { Name = "FRPS target", KeyHash = DmsKeyAuthenticationHandler.Hash(agent) };
        db.Machines.AddRange(target, new Machine { Name = "Other agent", KeyHash = DmsKeyAuthenticationHandler.Hash(otherAgent) });
        await db.SaveChangesAsync();

        var definition = MachineWorkflows.FrpsDeploy;
        const string secret = "frps-fixture-token";
        var requestId = Guid.NewGuid();
        OperationRequest Request(string value) => definition.Request(new FrpsDeployInput(value), "Test protected FRPS deployment",
            [target.Id], requestId: requestId);
        var item = await workflows.SubmitAsync(requester.Id, Request(secret));
        Assert.AreEqual(DecisionState.Pending, item.Decision);
        Assert.IsFalse(item.Parameters.Contains(secret, StringComparison.Ordinal));
        var frozenInput = JsonSerializer.Deserialize<OperationRequest>(item.Parameters)!.Input;
        var frozen = definition.ReadInput(frozenInput);
        Assert.IsNull(frozen.FrpsToken);
        Assert.IsNotNull(frozen.ProtectedToken);
        Assert.AreEqual(item.Id, (await workflows.SubmitAsync(requester.Id, Request(secret))).Id);
        await Assert.ThrowsAsync<DmsException>(() => workflows.SubmitAsync(requester.Id, Request("different")));

        item = await workflows.DecideAsync(approver.Id, item.Id, approve: true, "Test approval");
        var task = new WorkflowRun
        {
            ParentId = item.WorkflowId, RootId = item.WorkflowId!.Value, LockboxId = item.Id, MachineId = target.Id,
            IsMachineTask = true, Definition = definition.Name, Version = definition.Version,
            Input = frozenInput, State = RunState.Offered
        };
        db.WorkflowRuns.Add(task);
        await db.SaveChangesAsync();
        using var before = await ReadAsync(task.Id, agent);
        Assert.AreEqual(HttpStatusCode.Forbidden, before.StatusCode);
        task.State = RunState.Running;
        await db.SaveChangesAsync();
        using var foreign = await ReadAsync(task.Id, otherAgent);
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var approved = await ReadAsync(task.Id, agent);
        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode);
        Assert.AreEqual(new FrpsDeployToken(secret), await approved.Content.ReadFromJsonAsync<FrpsDeployToken>());
        task.State = RunState.Succeeded;
        await db.SaveChangesAsync();
        using var completed = await ReadAsync(task.Id, agent);
        Assert.AreEqual(HttpStatusCode.Forbidden, completed.StatusCode);
    }

    private async Task<HttpResponseMessage> ReadAsync(Guid task, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/agent/frps-deploy/{task:D}/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await Http.SendAsync(request);
    }
}
