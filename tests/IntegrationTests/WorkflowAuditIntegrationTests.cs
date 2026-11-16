using Aiursoft.DMS.Agent;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class WorkflowAuditIntegrationTests : TestBase
{
    private Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action) =>
        Server!.Services.GetRequiredService<ControllerGate>().RunAsync(async () =>
        {
            using var scope = Server.Services.CreateScope();
            return await action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>());
        });

    private async Task<(string User, string Reviewer, Guid Machine, string Key)> ArrangeAsync(bool automatic)
    {
        return await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var user = new User { UserName = "requester" + Guid.NewGuid().ToString("N"), Email = "requester@example.test", DisplayName = "Requester" }; db.Users.Add(user);
            var reviewer = new User { Id = Guid.NewGuid().ToString(), UserName = "audit-reviewer", DisplayName = "Audit reviewer" };
var key = DmsKeyAuthenticationHandler.CreateKey(true);
            var machine = new Machine { Name = "Audit Agent", KeyHash = DmsKeyAuthenticationHandler.Hash(key) };
            db.Users.Add(reviewer); db.Machines.Add(machine);
            DmsTestPermissions.Risk(db, user.Id, RiskLevel.Safe, request: true, automatic: automatic, approve: false);
            DmsTestPermissions.Risk(db, reviewer.Id, RiskLevel.Safe, request: false, automatic: false, approve: true);
            await db.SaveChangesAsync(); return (user.Id, reviewer.Id, machine.Id, key);
        });
    }

    private async Task<List<WorkflowAuditEntry>> HistoryAsync(Guid root) =>
        (await DatabaseAsync(db => db.WorkflowAuditOutbox.AsNoTracking().OrderBy(e => e.Id).ToListAsync()))
        .Select(e => e.Read()).Where(e => e.RootWorkflowId == root).ToList();

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RealAgentAndInternalProcessInheritExactlyOneApprovalContext(bool automatic)
    {
        var setup = await ArrangeAsync(automatic);
        var path = Directory.CreateTempSubdirectory("dms-audit-agent-").FullName;
        var journal = new TaskJournal(path);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var agent = new AgentClient(Http.BaseAddress!, setup.Key, journal).RunAsync(stop.Token);
        try
        {
            while (await DatabaseAsync(db => db.Machines.Where(m => m.Id == setup.Machine).Select(m => m.InstallationId).SingleAsync()) == null)
                await Task.Delay(50, stop.Token);
            using var scope = Server!.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
            var box = await service.SubmitAsync(setup.User, MachineWorkflows.DiskFree.Request(new EmptyInput(), "never-archive-reason", [setup.Machine]));
            if (!automatic) await service.DecideAsync(setup.Reviewer, box.Id, true, "never-archive-decision");
            while (await DatabaseAsync(db => db.WorkflowRuns.Where(r => r.Id == box.WorkflowId).Select(r => r.State).SingleAsync()) != RunState.Succeeded)
                await Task.Delay(50, stop.Token);
            var events = await HistoryAsync(box.WorkflowId);
            Assert.HasCount(1, events.Where(e => e.Kind == WorkflowAuditKind.LockboxSubmitted));
            Assert.HasCount(1, events.Where(e => e.Kind == WorkflowAuditKind.LockboxApproved));
            var created = events.Where(e => e.Kind == WorkflowAuditKind.WorkflowCreated).ToList(); Assert.HasCount(4, created);
            Assert.IsTrue(events.All(e => e.RequesterId == setup.User && e.LockboxId == box.Id));
            var approval = events.Single(e => e.Kind == WorkflowAuditKind.LockboxApproved);
            Assert.AreEqual(automatic, approval.AutomaticApproval); Assert.AreEqual(automatic ? null : setup.Reviewer, approval.ApproverId);
            var process = created.Single(e => e.Definition == WorkflowNames.Process);
            Assert.AreEqual(setup.Machine, process.MachineId); Assert.AreEqual(journal.InstallationId, process.InstallationId);
            var machine = created.Single(e => e.Definition == MachineWorkflows.DiskFree.Name);
            Assert.AreEqual(machine.WorkflowId, process.ParentWorkflowId); Assert.AreEqual(box.WorkflowId, process.RootWorkflowId);
            Assert.IsTrue(events.Any(e => e.WorkflowId == process.WorkflowId && e.State == RunState.Succeeded));
            Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(events).Contains("never-archive", StringComparison.Ordinal));
        }
        finally
        {
            await stop.CancelAsync(); try { await agent; } catch (OperationCanceledException) { }
            journal.Dispose(); Directory.Delete(path, true);
        }
    }

    [TestMethod]
    public async Task DenialAndIdempotentSubmissionDoNotFabricateExecutionOrDuplicateApprovalFacts()
    {
        var setup = await ArrangeAsync(false);
        using var scope = Server!.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<WorkflowService>();
        var request = MachineWorkflows.DiskFree.Request(new EmptyInput(), "Inspect", [setup.Machine]);
        var box = await service.SubmitAsync(setup.User, request); await service.DecideAsync(setup.Reviewer, box.Id, false, "Denied");
        var before = await HistoryAsync(box.WorkflowId);
        Assert.AreEqual(box.Id, (await service.SubmitAsync(setup.User, request)).Id);
        Assert.AreEqual(409, (await Assert.ThrowsAsync<DmsException>(() => service.DecideAsync(setup.Reviewer, box.Id, true, "Too late"))).StatusCode);
        var after = await HistoryAsync(box.WorkflowId); Assert.HasCount(before.Count, after);
        Assert.HasCount(1, after.Where(e => e.Kind == WorkflowAuditKind.WorkflowCreated));
        var denied = after.Single(e => e.Kind == WorkflowAuditKind.LockboxDenied);
        Assert.AreEqual(setup.Reviewer, denied.ApproverId); Assert.AreEqual(DecisionState.Denied, denied.Decision);
        Assert.AreEqual(MachineWorkflows.DiskFree.Name, denied.RequestedDefinition);
        Assert.AreEqual(MachineWorkflows.DiskFree.Version, denied.RequestedVersion);
        Assert.IsTrue(after.Any(e => e.State == RunState.Denied)); Assert.IsFalse(after.Any(e => e.State == RunState.Succeeded));
    }
}
