using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Authentication;
using Microsoft.AspNetCore.Identity;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class MobileAuthTests : TestBase
{
    [TestMethod]
    public async Task ConfigEndpointAdvertisesLocalAuthenticationAndApiKeySupport()
    {
        using var response = await Http.GetAsync("/api/v1/config");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var json = JObject.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("Local", json["AuthenticationMode"]?.ToString());
        Assert.AreEqual(true, json["SupportsPersonalApiKey"]?.Value<bool>());
        Assert.AreEqual(true, json["AllowRegistration"]?.Value<bool>());
    }

    [TestMethod]
    public async Task LocalMobileTokenAuthenticatesLockboxReviewDecisionAndUpdates()
    {
        string reviewerId;
        string requesterId;
        string reviewerEmail = $"reviewer-{Guid.NewGuid():N}@example.test";
        const string password = "Password123!";
        Guid lockboxId;

        using (var scope = Server!.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();

            var reviewer = new User { UserName = reviewerEmail, Email = reviewerEmail, DisplayName = "Mobile Reviewer" };
            Assert.IsTrue((await users.CreateAsync(reviewer, password)).Succeeded);
            reviewerId = reviewer.Id;

            var role = new IdentityRole("mobile-reviewers-" + Guid.NewGuid().ToString("N"));
            Assert.IsTrue((await roles.CreateAsync(role)).Succeeded);
            Assert.IsTrue((await roles.AddClaimAsync(role, new Claim(AppPermissions.Type, AppPermissionNames.CanReadDmsOperations))).Succeeded);
            Assert.IsTrue((await roles.AddClaimAsync(role, new Claim(AppPermissions.Type, AppPermissionNames.CanApproveSafeRiskWorkflow))).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(reviewer, role.Name!)).Succeeded);

            var requester = new User { UserName = "req-" + Guid.NewGuid().ToString("N"), Email = $"req-{Guid.NewGuid():N}@example.test", DisplayName = "Requester" };
            Assert.IsTrue((await users.CreateAsync(requester)).Succeeded);
            requesterId = requester.Id;

            var box = new LockboxRequest
            {
                RequesterId = requesterId,
                ClientRequestId = Guid.NewGuid(),
                Definition = WorkflowNames.AgentStatus,
                Risk = RiskLevel.Safe,
                Version = MachineWorkflows.AgentStatus.Version,
                CreatedAt = DateTime.UtcNow,
                Decision = DecisionState.Pending,
                Parameters = System.Text.Json.JsonSerializer.Serialize(new OperationRequest())
            };
            db.Lockboxes.Add(box);
            await db.SaveChangesAsync();
            lockboxId = box.Id;
        }

        // Wrong password is rejected.
        using (var badLogin = await Http.PostAsJsonAsync("/api/v1/auth/local/login", new { EmailOrUserName = reviewerEmail, Password = "wrong-password" }))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, badLogin.StatusCode);
        }

        // Valid login issues a dmsl_ local bearer token.
        using var loginResponse = await Http.PostAsJsonAsync("/api/v1/auth/local/login", new { EmailOrUserName = reviewerEmail, Password = password });
        Assert.AreEqual(HttpStatusCode.OK, loginResponse.StatusCode);
        var loginJson = JObject.Parse(await loginResponse.Content.ReadAsStringAsync());
        var accessToken = loginJson["AccessToken"]?.ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(accessToken));
        StringAssert.StartsWith(accessToken, LocalApiAuthenticationDefaults.TokenPrefix);

        // Mobile token works on identity, review queue, review updates, decision, and review history.
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using (var whoami = await Http.GetAsync("/api/v1/dms/access/identity"))
        {
            Assert.AreEqual(HttpStatusCode.OK, whoami.StatusCode);
            var user = JObject.Parse(await whoami.Content.ReadAsStringAsync())["Data"]!.ToObject<CurrentUser>()!;
            Assert.AreEqual(reviewerId, user.Id);
        }

        using (var queueResponse = await Http.GetAsync("/api/v1/dms/lockboxes/review"))
        {
            Assert.AreEqual(HttpStatusCode.OK, queueResponse.StatusCode);
            var page = JObject.Parse(await queueResponse.Content.ReadAsStringAsync())["Data"]!.ToObject<LockboxReviewPage>()!;
            Assert.IsTrue(page.Entries.Any(entry => entry.LockboxId == new LockboxId(lockboxId)));
        }

        using (var updatesResponse = await Http.GetAsync("/api/v1/dms/lockboxes/review/updates"))
        {
            Assert.AreEqual(HttpStatusCode.OK, updatesResponse.StatusCode);
            var update = JObject.Parse(await updatesResponse.Content.ReadAsStringAsync())["Data"]!.ToObject<LockboxReviewUpdate>()!;
            Assert.IsNotNull(update.LatestPendingAt);
        }

        using (var decisionResponse = await Http.PostAsJsonAsync($"/api/v1/dms/lockboxes/lb/{lockboxId}/decision", new { Approve = false, Reason = "Denied from mobile" }))
        {
            Assert.AreEqual(HttpStatusCode.OK, decisionResponse.StatusCode);
        }

        using (var historyResponse = await Http.GetAsync("/api/v1/dms/lockboxes/review/history"))
        {
            Assert.AreEqual(HttpStatusCode.OK, historyResponse.StatusCode);
            var history = JObject.Parse(await historyResponse.Content.ReadAsStringAsync())["Data"]!.ToObject<LockboxHistoryPage>()!;
            Assert.IsTrue(history.Entries.Any(entry => entry.LockboxId == new LockboxId(lockboxId) && entry.Decision == DecisionState.Denied));
        }

        // Tampered local token and malformed dmsu_ key are rejected.
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalApiAuthenticationDefaults.TokenPrefix + "invalid");
        using (var invalidLocal = await Http.GetAsync("/api/v1/dms/lockboxes/review"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidLocal.StatusCode);
        }

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "dmsu_short");
        using (var invalidDmsKey = await Http.GetAsync("/api/v1/dms/lockboxes/review"))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, invalidDmsKey.StatusCode);
        }
    }

    [TestMethod]
    public async Task LocalMobileRegisterIssuesValidToken()
    {
        var email = $"mobile-reg-{Guid.NewGuid():N}@example.test";
        using var registerResponse = await Http.PostAsJsonAsync("/api/v1/auth/local/register", new { Email = email, Password = "Password123!" });
        Assert.AreEqual(HttpStatusCode.OK, registerResponse.StatusCode);
        var json = JObject.Parse(await registerResponse.Content.ReadAsStringAsync());
        var token = json["AccessToken"]?.ToString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
        StringAssert.StartsWith(token, LocalApiAuthenticationDefaults.TokenPrefix);

        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var whoami = await Http.GetAsync("/api/v1/dms/access/identity");
        Assert.AreEqual(HttpStatusCode.OK, whoami.StatusCode);
    }
}
