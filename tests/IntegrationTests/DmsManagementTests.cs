using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class DmsManagementTests : TestBase
{
    private async Task<(string Manager, string ManagerKey, string Employee, string EmployeeKey)> ArrangeAsync()
    {
        using var scope = Server!.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var suffix = Guid.NewGuid().ToString("N");
        var manager = new User { UserName = "manager" + suffix, Email = "manager" + suffix + "@example.test", DisplayName = "Manager" };
        var employee = new User { UserName = "employee" + suffix, Email = "employee" + suffix + "@example.test", DisplayName = "Employee" };
        var createdManager = await users.CreateAsync(manager); var createdEmployee = await users.CreateAsync(employee);
        Assert.IsTrue(createdManager.Succeeded, string.Join("; ", createdManager.Errors.Select(e => e.Description)));
        Assert.IsTrue(createdEmployee.Succeeded, string.Join("; ", createdEmployee.Errors.Select(e => e.Description)));
        Assert.IsTrue((await users.AddClaimAsync(manager, new System.Security.Claims.Claim(AppPermissions.Type, "CanManageDmsAccess"))).Succeeded);
        Assert.IsTrue((await users.AddClaimAsync(employee, new System.Security.Claims.Claim(AppPermissions.Type, AppPermissionNames.CanReadUsers))).Succeeded);
        var managerKey = DmsKeyAuthenticationHandler.CreateKey(false); var employeeKey = DmsKeyAuthenticationHandler.CreateKey(false);
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        DmsTestPermissions.DeactivatePreviousMachines(db);
        db.PersonalApiKeys.AddRange(
            new PersonalApiKey { UserId = manager.Id, Name = "Manager", KeyHash = DmsKeyAuthenticationHandler.Hash(managerKey), ExpiresAt = DateTime.UtcNow.AddHours(1) },
            new PersonalApiKey { UserId = employee.Id, Name = "Employee", KeyHash = DmsKeyAuthenticationHandler.Hash(employeeKey), ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();
        return (manager.Id, managerKey, employee.Id, employeeKey);
    }

    private async Task<T> CliAsync<T>(string key, params string[] arguments)
    {
        var cli = await Client.DmsCommandApp.Create().TestRunAsync([.. arguments, "--server", Http.BaseAddress!.ToString(), "--api-key", key, "--output-format", "json"]);
        Assert.AreEqual(0, cli.ProgramReturn, cli.StdErr);
        return JObject.Parse(cli.StdOut)["Data"]!.ToObject<T>()!;
    }

    [TestMethod]
    public async Task ManagementIdentityDoesNotImplicitlyGrantOperationalPermissions()
    {
        var setup = await ArrangeAsync();
        var identity = await CliAsync<CurrentUser>(setup.ManagerKey, "access", "whoami");
        Assert.AreEqual(setup.Manager, identity.Id);
        StringAssert.StartsWith(identity.UserName, "manager");
        Assert.IsFalse(identity.CanApproveOwnRequests);
        Assert.IsTrue(identity.RiskPermissions.All(p => !p.CanRequest && !p.AutoApprove && !p.CanApprove));
        CollectionAssert.Contains(identity.Permissions, "CanManageDmsAccess");
        var snapshot = await CliAsync<AccessSnapshot>(setup.ManagerKey, "access", "get");
        Assert.IsNotNull(snapshot.Machines);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        var request = ClusterWorkflows.GetMachines.Request(new EmptyInput(), "");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/lockboxes", request)).StatusCode);
    }

    [TestMethod]
    public async Task RemovedResourceRoutesAndCliArgumentsAreUnavailable()
    {
        var setup = await ArrangeAsync();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.ManagerKey);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync("/api/v1/dms/clusters")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await Http.PostAsJsonAsync("/api/v1/dms/clusters", new { Name = "Removed" })).StatusCode);
        foreach (var args in new[] { new[] { "cluster", "list" }, new[] { "operation", "list-definitions", "--cluster-id", Guid.NewGuid().ToString() } })
        {
            var result = await Client.DmsCommandApp.Create().TestRunAsync(args);
            Assert.AreNotEqual(0, result.ProgramReturn);
        }
        await LoginAsAdmin();
        Assert.DoesNotContain("Create cluster", await Http.GetStringAsync("/Dms"));
    }

    [TestMethod]
    public async Task WebDoesNotExposeAgentKeyReplacement()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var originalHash = DmsKeyAuthenticationHandler.Hash(DmsKeyAuthenticationHandler.CreateKey(true));
        var machine = new Machine { Name = "Demo Global", KeyHash = originalHash };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var machines = await Http.GetStringAsync("/Dms");
        Assert.DoesNotContain("/DmsAccess/RotateAgentKey", machines);
        Assert.DoesNotContain("Machine connection recovery", machines);
        var detail = await Http.GetStringAsync($"/Dms/Machine/{machine.Id}");
        StringAssert.Contains(detail, machine.Name);
        StringAssert.Contains(detail, "Workflow history");
        Assert.DoesNotContain("/DmsAccess/RotateAgentKey", detail);

        var replacement = $"/DmsAccess/RotateAgentKey/{machine.Id}";
        foreach (var removed in new[] { "/DmsAccess", "/DmsAccess/Index", replacement })
        {
            Assert.AreEqual(HttpStatusCode.NotFound, (await Http.GetAsync(removed)).StatusCode, removed);
        }
        foreach (var removed in new[] { "/DmsAccess/AgentKey", replacement })
        {
            var form = new Dictionary<string, string> { ["machineId"] = machine.Id.ToString(), ["confirmationName"] = machine.Name };
            Assert.AreEqual(HttpStatusCode.NotFound, (await PostForm(removed, form, "/DmsAccess/Keys")).StatusCode, removed);
        }

        var saved = await db.Machines.AsNoTracking().SingleAsync(m => m.Id == machine.Id);
        Assert.AreEqual(originalHash, saved.KeyHash);
        Assert.IsFalse(await db.AuditEvents.AnyAsync(a => a.Action == "key.agent.rotate" && a.ResourceId == machine.Id));
    }

    [TestMethod]
    public async Task WebFiltersMachineConnectionsAndShowsOnlyTheLatestScheduledBaselineForTheMachine()
    {
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var prefix = $"fléet-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        Machine AddMachine(string suffix, DateTime? lastSeen, bool disabled = false)
        {
            var machine = new Machine { Name = $"{prefix}-{suffix}", KeyHash = Guid.NewGuid().ToString("N"), LastSeen = lastSeen, Disabled = disabled };
            db.Machines.Add(machine);
            return machine;
        }
        var online = AddMachine("online", now);
        var offline = AddMachine("offline", now.AddMinutes(-5));
        var never = AddMachine("never", null);
        var disabled = AddMachine("disabled", now, disabled: true);

        WorkflowRun AddRun(Machine machine, DateTime createdAt, RunState state, WorkflowOrigin origin = WorkflowOrigin.Automatic,
            string definition = WorkflowNames.ApplySecurityBaseline, Guid? parentId = null)
        {
            var run = new WorkflowRun { MachineId = machine.Id, Definition = definition, Origin = origin,
                StartedAt = createdAt, FinishedAt = createdAt.AddSeconds(5), CreatedAt = createdAt, State = state,
                Result = "{\"Summary\":\"Baseline fixture result\"}", ParentId = parentId };
            run.RootId = parentId ?? run.Id;
            db.WorkflowRuns.Add(run);
            return run;
        }
        var previous = AddRun(offline, now.AddHours(-3), RunState.Succeeded);
        var latest = AddRun(offline, now.AddHours(-2), RunState.Failed);
        var manual = AddRun(offline, now.AddHours(-1), RunState.Succeeded, WorkflowOrigin.Manual);
        var child = AddRun(offline, now.AddHours(-1), RunState.Succeeded, parentId: latest.Id);
        var otherMachine = AddRun(online, now.AddHours(-1), RunState.Succeeded);
        var otherWorkflow = AddRun(offline, now.AddHours(-1), RunState.Succeeded, definition: WorkflowNames.AgentStatus);
        await db.SaveChangesAsync();

        foreach (var (connection, expected) in new[] { ("Online", online), ("Offline", offline), ("NeverConnected", never), ("Disabled", disabled) })
        {
            var listing = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Dms?q={prefix.ToUpperInvariant()}&connection={connection}"));
            StringAssert.Contains(listing, expected.Name);
            StringAssert.Contains(listing, $"data-machine-connection=\"{connection}\"");
            foreach (var other in new[] { online, offline, never, disabled }.Where(machine => machine != expected))
                Assert.DoesNotContain(other.Name, listing);
        }
        StringAssert.Contains(await Http.GetStringAsync($"/Dms?q={prefix}-unknown"), "No machines match these filters.");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/Dms?connection=123")).StatusCode);

        var detail = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Dms/Machine/{offline.Id}"));
        StringAssert.Contains(detail, offline.Name);
        StringAssert.Contains(detail, "Baseline fixture result");
        StringAssert.Contains(detail, "data-run-state=\"Failed\"");
        StringAssert.Contains(detail, latest.Id.ToString());

        var baselineSection = detail.Substring(detail.IndexOf("id=\"baseline-heading\"", StringComparison.Ordinal));
        baselineSection = baselineSection.Substring(0, baselineSection.IndexOf("</section>", StringComparison.Ordinal));
        StringAssert.Contains(baselineSection, latest.Id.ToString());
        foreach (var run in new[] { previous, manual, child, otherMachine, otherWorkflow })
            Assert.DoesNotContain(run.Id.ToString(), baselineSection);

        StringAssert.Contains(detail, "Recent executions");
        StringAssert.Contains(detail, "Investigate machine");
        StringAssert.Contains(detail, $"/Dms/Investigation?machineId={offline.Id}");
        StringAssert.Contains(detail, "excludeHeartbeats=true");
        StringAssert.Contains(detail, manual.Id.ToString());
        StringAssert.Contains(detail, child.Id.ToString());
        StringAssert.Contains(detail, previous.Id.ToString());
        Assert.DoesNotContain(otherMachine.Id.ToString(), detail);
        Assert.DoesNotContain(otherWorkflow.Id.ToString(), detail);

        var neverDetail = WebUtility.HtmlDecode(await Http.GetStringAsync($"/Dms/Machine/{never.Id}"));
        StringAssert.Contains(neverDetail, "No scheduled baseline execution is recorded for this machine.");
        StringAssert.Contains(neverDetail, "No recent workflow executions recorded for this machine.");
    }

    [TestMethod]
    public async Task MachineSshOverviewKeepsLeaseIdentityFreshAndShowsRenewedEvidence()
    {
        Assert.AreEqual(HttpStatusCode.Redirect, (await Http.GetAsync($"/Dms/MachineSshStates?id={Guid.NewGuid()}")).StatusCode);
        await LoginAsAdmin();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var now = DateTimeOffset.UtcNow;
        var user = new User { UserName = "operator", DisplayName = "Lease operator" };
        db.Users.Add(user);
        var machine = new Machine { Name = "Lease preview", KeyHash = "test-lease-host", LastSeen = now.UtcDateTime };
        var offline = new Machine { Name = "Offline preview", KeyHash = "test-lease-offline", LastSeen = now.AddMinutes(-1).UtcDateTime };
        db.Machines.AddRange(machine, offline);
        var open = OperationsWorkflows.SshOpen.Name;
        var check = OperationsWorkflows.SshCheckLease.Name;
        var box = new LockboxRequest { Definition = open, RequesterId = user.Id, ClientRequestId = Guid.NewGuid(), Decision = DecisionState.Approved };
        db.Lockboxes.Add(box);
        var leaseId = Guid.NewGuid();
        var observation = new SshHostObservation(now.AddMinutes(-2), OrdinaryOpen: true, LeaseKnown: true, leaseId, now.AddMinutes(20));

        void AddObservation(Guid id, string definition, SshHostObservation status, Guid? lockbox = null, Guid? runId = null)
        {
            var result = definition == check
                ? JsonSerializer.Serialize(new SshLeaseCheckResult("Probe", null) { Observation = status })
                : JsonSerializer.Serialize(new OperationResult("Change", Facts: JsonSerializer.SerializeToElement(new { SshObservation = status })));
            db.WorkflowRuns.Add(new WorkflowRun
            {
                Id = runId ?? Guid.NewGuid(), Definition = definition, MachineId = id, LockboxId = lockbox, State = RunState.Succeeded,
                CreatedAt = status.ObservedAtUtc.UtcDateTime, FinishedAt = now.UtcDateTime, Result = result
            });
        }

        AddObservation(machine.Id, open, observation, box.Id, leaseId);
        AddObservation(offline.Id, check, observation);
        await db.SaveChangesAsync();

        var page = WebUtility.HtmlDecode(await Http.GetStringAsync("/Dms?q=Lease%20preview"));
        StringAssert.Contains(page, "Temporary SSH open");
        StringAssert.Contains(page, "Ordinary SSH open");
        StringAssert.Contains(page, "Lease operator");
        StringAssert.Contains(page, box.Id.ToString());
        StringAssert.Contains(page, "data-utc-now");
        var pollPath = $"/Dms/MachineSshStates?id={machine.Id}";
        var off = await Http.GetStringAsync($"/Dms/MachineSshStates?id={offline.Id}");
        StringAssert.Contains(off, "SSH status unverified");
        Assert.DoesNotContain("Lease operator", off);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await Http.GetAsync("/Dms/MachineSshStates?id=invalid")).StatusCode);

        AddObservation(machine.Id, check, observation with { ObservedAtUtc = now.AddMinutes(-1), ExpiresAtUtc = now.AddSeconds(-10) });
        await db.SaveChangesAsync();
        var expired = await Http.GetStringAsync(pollPath);
        StringAssert.Contains(expired, "data-ssh-after-expiry>SSH lease expired, awaiting confirmation");
        AddObservation(machine.Id, OperationsWorkflows.SshDisable.Name, new SshHostObservation(now, OrdinaryOpen: false, LeaseKnown: true));
        await db.SaveChangesAsync();
        var closed = await Http.GetStringAsync(pollPath);
        StringAssert.Contains(closed, "SSH closed");
        Assert.DoesNotContain("Lease operator", closed);
    }

    [TestMethod]
    public async Task ReadPermissionAndAgentKeysCannotMutateControllerAccess()
    {
        var setup = await ArrangeAsync();
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", setup.EmployeeKey);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/access")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync("/api/v1/dms/access/join-keys", new JoinKeyInput("Forbidden"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.PostAsJsonAsync($"/api/v1/dms/access/machines/{Guid.NewGuid()}/key", new { })).StatusCode);
        var own = await CliAsync<CurrentUser>(setup.EmployeeKey, "access", "whoami");
        Assert.AreEqual(setup.Employee, own.Id);
        Assert.IsFalse(own.CanApproveOwnRequests);
    }

    [TestMethod]
    public async Task KeyRotationDoesNotExposeStoredSecrets()
    {
        var setup = await ArrangeAsync();
        Guid machine;
        using (var scope = Server!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>(); var row = new Machine { Name = "Registered fixture" };
            db.Machines.Add(row); await db.SaveChangesAsync(); machine = row.Id;
        }
        var first = await CliAsync<AgentKeyResult>(setup.ManagerKey, "access", "issue-agent-key", "--machine-id", machine.ToString());
        var second = await CliAsync<AgentKeyResult>(setup.ManagerKey, "access", "issue-agent-key", "--machine-id", machine.ToString());
        Assert.AreNotEqual(first.Key, second.Key); Assert.AreEqual(machine, second.MachineId);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first.Key);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await Http.GetAsync("/api/agent/channel")).StatusCode);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.Key);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await Http.GetAsync("/api/v1/dms/access/identity")).StatusCode);
        var snapshotWithMachine = await CliAsync<AccessSnapshot>(setup.ManagerKey, "access", "get");
        var machineInfo = snapshotWithMachine.Machines.Single(m => m.Id == machine);
        Assert.AreEqual("Registered fixture", machineInfo.Name); Assert.IsFalse(machineInfo.Disabled);
        var snapshotJson = Newtonsoft.Json.JsonConvert.SerializeObject(snapshotWithMachine);
        Assert.DoesNotContain("KeyHash", snapshotJson); Assert.DoesNotContain(second.Key, snapshotJson);
        using var check = Server!.Services.CreateScope(); var verify = check.ServiceProvider.GetRequiredService<TemplateDbContext>();
        Assert.AreEqual(DmsKeyAuthenticationHandler.Hash(second.Key), (await verify.Machines.SingleAsync(m => m.Id == machine)).KeyHash);
        Assert.IsFalse(await verify.AuditEvents.AnyAsync(a => a.Detail.Contains(second.Key) || a.Detail.Contains(first.Key)));
        Assert.AreEqual(0, await verify.Lockboxes.CountAsync(l => l.RequesterId == setup.Manager));
    }

}
