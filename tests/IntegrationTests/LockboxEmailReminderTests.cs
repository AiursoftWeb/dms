using System.Net.Mail;
using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public sealed class LockboxEmailReminderTests : TestBase
{
    [TestMethod]
    public async Task NotifiesEligibleVerifiedReviewersOnlyAfterFifteenMinutesAndRemembersDelivery()
    {
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var definition = MachineWorkflows.AgentStatus;
        var approval = DmsPermissions.Approve(definition.Risk);
        var view = AppPermissionNames.CanReadDmsOperations;
        var role = new IdentityRole("email-review-" + Guid.NewGuid());
        Assert.IsTrue((await roles.CreateAsync(role)).Succeeded);
        foreach (var permission in new[] { approval, view })
            Assert.IsTrue((await roles.AddClaimAsync(role, new Claim(AppPermissions.Type, permission))).Succeeded);

        var createdUsers = new List<User>();
        async Task<User> UserAsync(bool verified, params string[] permissions)
        {
            var name = "email-review-" + Guid.NewGuid().ToString("N");
            var user = new User { UserName = name, DisplayName = "Email reviewer", Email = name + "@example.test", EmailConfirmed = verified };
            createdUsers.Add(user);
            Assert.IsTrue((await users.CreateAsync(user)).Succeeded);
            foreach (var permission in permissions)
                Assert.IsTrue((await users.AddClaimAsync(user, new Claim(AppPermissions.Type, permission))).Succeeded);
            return user;
        }

        var requester = await UserAsync(true, approval, view);
        var direct = await UserAsync(true, approval, view);
        var viaRole = await UserAsync(true);
        Assert.IsTrue((await users.AddToRoleAsync(viaRole, role.Name!)).Succeeded);
        _ = await UserAsync(false, approval, view);
        _ = await UserAsync(true, approval);
        _ = await UserAsync(true, view);
        var locked = await UserAsync(true, approval, view);
        Assert.IsTrue((await users.SetLockoutEnabledAsync(locked, true)).Succeeded);
        Assert.IsTrue((await users.SetLockoutEndDateAsync(locked, DateTimeOffset.UtcNow.AddDays(1))).Succeeded);
        var earliest = await db.Lockboxes.MinAsync(box => (DateTime?)box.CreatedAt);
        var now = earliest?.AddMinutes(-1) ?? DateTime.UtcNow;
        LockboxRequest Request(DecisionState decision, string version, double age) => new()
        {
            ClientRequestId = Guid.NewGuid(), RequesterId = requester.Id, Definition = definition.Name,
            Risk = definition.Risk, Version = version, CreatedAt = now.AddMinutes(-age), Decision = decision
        };
        var due = Request(DecisionState.Pending, definition.Version, 15);
        var target = Guid.NewGuid();
        due.TargetMachines = $"[\"{target:D}\"]";
        due.Reason = "private-reason-fixture";
        due.Parameters = "{\"Input\":\"private-parameter-fixture\"}";
        var notYetSwept = Request(DecisionState.Pending, definition.Version, LockboxExpiry.RequestLifetime.TotalMinutes + 1);
        notYetSwept.ExpiresAt = now.AddMinutes(-1);
        LockboxRequest[] requests = [due, Request(DecisionState.Pending, definition.Version, 14),
            Request(DecisionState.Approved, definition.Version, 16), Request(DecisionState.Denied, definition.Version, 16),
            Request(DecisionState.Withdrawn, definition.Version, 16), Request(DecisionState.Pending, "retired-version", 16),
            Request(DecisionState.Expired, definition.Version, 241), notYetSwept];
        db.Lockboxes.AddRange(requests);
        await db.SaveChangesAsync();
        var mail = new RecordingMailer(viaRole.Email!);
        async Task ScanAsync()
        {
            using var scanScope = Server!.Services.CreateScope();
            var services = scanScope.ServiceProvider;
            var reminders = new LockboxEmailReminders(services.GetRequiredService<TemplateDbContext>(),
                services.GetRequiredService<WorkflowService>(), services.GetRequiredService<AccessService>(),
                mail, NullLogger<LockboxEmailReminders>.Instance);
            await reminders.SendDueAsync(now, CancellationToken.None);
        }
        try
        {
            await ScanAsync();
            CollectionAssert.AreEqual(new[] { direct.Email! }, mail.Delivered);
            await ScanAsync();
            await ScanAsync();
            CollectionAssert.AreEquivalent(new[] { direct.Email!, viaRole.Email! }, mail.Delivered);
            Assert.HasCount(2, mail.Summaries);
            foreach (var summary in mail.Summaries)
            {
                Assert.AreEqual(new LockboxId(due.Id), summary.Id);
                Assert.AreEqual(1, summary.TargetCount);
                var (subject, body) = SmtpLockboxReminderEmail.Format(summary, new Uri("https://dms.example.test"));
                StringAssert.Contains(subject, $"[{definition.Risk}] {definition.Name}");
                StringAssert.Contains(body, $"申请人：Email reviewer (@{requester.UserName})");
                StringAssert.Contains(body, $"提交时间：{due.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
                StringAssert.Contains(body, $"过期时间：{due.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");
                StringAssert.Contains(body, $"https://dms.example.test/Dms/Lockbox/{due.Id:D}");
                Assert.DoesNotContain(due.Reason, body);
                Assert.DoesNotContain("private-parameter-fixture", body);
                Assert.DoesNotContain(target.ToString(), body);
            }
        }
        finally
        {
            var ids = requests.Select(request => request.Id).ToArray();
            db.AuditEvents.RemoveRange(db.AuditEvents.Where(entry => entry.ResourceId.HasValue && ids.Contains(entry.ResourceId.Value)));
            db.Lockboxes.RemoveRange(requests);
            await db.SaveChangesAsync();
            foreach (var user in createdUsers) await users.DeleteAsync(user);
            await roles.DeleteAsync(role);
        }
    }

    private sealed class RecordingMailer(string failOnce) : ILockboxReminderEmail
    {
        private bool _failed;
        public List<string> Delivered { get; } = [];
        public List<LockboxReminderSummary> Summaries { get; } = [];

        public Task SendAsync(MailAddress recipient, LockboxReminderSummary summary, CancellationToken token)
        {
            if (!_failed && recipient.Address == failOnce)
            {
                _failed = true;
                throw new SmtpException("Transient test failure");
            }
            Delivered.Add(recipient.Address);
            Summaries.Add(summary);
            return Task.CompletedTask;
        }
    }
}
