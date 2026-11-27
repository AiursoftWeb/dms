using System.Net;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class AptMaintenanceIntegrationTests : TestBase
{
    [TestMethod]
    public async Task PreviewAndApprovalFreezeDifferentPlansForEachMachineAndExcludeNewMachines()
    {
        var (user, key, first, second) = await DatabaseAsync(async db =>
        {
            DmsTestPermissions.DeactivatePreviousMachines(db);
            var requester = new User { UserName = "apt-" + Guid.NewGuid(), DisplayName = "APT fixture" };
            var apiKey = DmsKeyAuthenticationHandler.CreateKey(false);
            var firstMachine = Machine("APT first");
            var secondMachine = Machine("APT second");
            db.Users.Add(requester);
            db.Machines.AddRange(firstMachine, secondMachine);
            db.PersonalApiKeys.Add(new PersonalApiKey { UserId = requester.Id, Name = "APT fixture",
                KeyHash = DmsKeyAuthenticationHandler.Hash(apiKey), ExpiresAt = DateTime.UtcNow.AddHours(1) });
            DmsTestPermissions.Risk(db, requester.Id, RiskLevel.Low, request: true, automatic: true);
            DmsTestPermissions.Risk(db, requester.Id, RiskLevel.Medium, request: true);
            await db.SaveChangesAsync();
            return (requester.Id, apiKey, firstMachine.Id, secondMachine.Id);
        });

        var previewRequest = Guid.NewGuid();
        Assert.AreEqual(0, (await CliAsync("upgrade-all", "--all-machines", "--dry-run", "--reason", "Preview upgrades",
            "--request-id", previewRequest.ToString())).ProgramReturn);
        var preview = await DatabaseAsync(db => db.Lockboxes.SingleAsync(box => box.ClientRequestId == previewRequest));
        Assert.AreEqual(OperationsWorkflows.AptPlan.Name, preview.Definition);
        Assert.AreEqual(DecisionState.Approved, preview.Decision);
        var previewId = preview.WorkflowId!.Value;

        await DatabaseAsync(async db =>
        {
            var root = await db.WorkflowRuns.SingleAsync(run => run.Id == previewId);
            var children = await db.WorkflowRuns.Where(run => run.ParentId == previewId).ToListAsync();
            foreach (var (machine, package) in new[] { (first, "lib-first"), (second, "lib-second") })
            {
                var child = children.SingleOrDefault(run => run.MachineId == machine);
                if (child == null)
                {
                    child = new WorkflowRun { RootId = root.Id, ParentId = root.Id, LockboxId = preview.Id,
                        MachineId = machine, IsMachineTask = true, Definition = OperationsWorkflows.AptPlan.Name,
                        Version = OperationsWorkflows.AptPlan.Version };
                    db.WorkflowRuns.Add(child);
                }
                child.State = RunState.Succeeded;
                child.Result = JsonSerializer.Serialize(new AptPlanResult([new(AptPackageAction.Upgrade, package, "1", "2")], []));
            }
            root.State = RunState.Succeeded;
            db.Machines.Add(Machine("APT newly added"));
            return await db.SaveChangesAsync();
        });

        var plan = await CliAsync("plan", previewId.ToString(), "--output-format", "json");
        Assert.AreEqual(0, plan.ProgramReturn, plan.StdErr);
        StringAssert.Contains(plan.StdOut, "lib-first");
        StringAssert.Contains(plan.StdOut, "lib-second");
        Assert.AreNotEqual(0, (await CliAsync("upgrade-all", "--all-machines", "--plan", previewId.ToString(),
            "--reason", "Must not expand scope")).ProgramReturn);
        Assert.AreNotEqual(0, (await CliAsync("autoremove", "--purge", "--plan", previewId.ToString(),
            "--reason", "Must match preview")).ProgramReturn);

        var applyRequest = Guid.NewGuid();
        Assert.AreEqual(0, (await CliAsync("upgrade-all", "--plan", previewId.ToString(), "--reason", "Upgrade reviewed packages",
            "--request-id", applyRequest.ToString())).ProgramReturn);
        var request = await DatabaseAsync(db => db.Lockboxes.SingleAsync(box => box.ClientRequestId == applyRequest));
        Assert.AreEqual(user, request.RequesterId);
        Assert.AreEqual(DecisionState.Pending, request.Decision);
        CollectionAssert.AreEquivalent(new[] { first, second }, JsonSerializer.Deserialize<Guid[]>(request.TargetMachines)!);
        Assert.IsNull(request.WorkflowId);

        await LoginAsAdmin();
        var lockbox = await Http.GetAsync("/Dms/Lockbox?id=" + request.Id);
        Assert.AreEqual(HttpStatusCode.OK, lockbox.StatusCode);
        var html = await lockbox.Content.ReadAsStringAsync();
        StringAssert.Contains(html, "APT package plan");
        StringAssert.Contains(html, "lib-first");
        StringAssert.Contains(html, "lib-second");

        var approved = await PostForm("/Dms/Decide", new Dictionary<string, string>
        {
            ["id"] = ((LockboxId)request.Id).ToString(), ["approve"] = "true", ["reason"] = "Reviewed both plans"
        }, "/Dms/Lockbox?id=" + request.Id);
        Assert.AreEqual(HttpStatusCode.Redirect, approved.StatusCode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var runs = await DatabaseAsync(db => db.WorkflowRuns.AsNoTracking()
                .Where(run => run.LockboxId == request.Id && run.IsMachineTask).ToListAsync(timeout.Token));
            if (runs.Count == 2)
            {
                foreach (var run in runs)
                {
                    var input = OperationsWorkflows.AptUpgradeAll.ReadInput(run.Input);
                    Assert.AreEqual(previewId, input.PlanId);
                    Assert.AreEqual(run.MachineId == first ? "lib-first" : "lib-second", input.Changes!.Single().Package);
                }
                break;
            }
            await Task.Delay(50, timeout.Token);
        }

        async Task<(int ProgramReturn, string StdOut, string StdErr)> CliAsync(params string[] arguments)
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync(["machine", "apt", .. arguments,
                "--dms-server-endpoint", Http.BaseAddress!.ToString(), "--api-key", key]);
            return (result.ProgramReturn, result.StdOut, result.StdErr);
        }
    }

    private static Machine Machine(string name) => new() { Name = name,
        KeyHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true)) };

    private async Task<T> DatabaseAsync<T>(Func<TemplateDbContext, Task<T>> action)
    {
        using var scope = Server!.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ControllerGate>()
            .RunAsync(() => action(scope.ServiceProvider.GetRequiredService<TemplateDbContext>()));
    }
}
