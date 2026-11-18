using System.Net;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class PersonalSshKeysTests : TestBase
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHpyp+C8Ia2B6RVM1UCr5HG+EtGR2xz5OF05P9kPz84D";

    [TestMethod]
    public async Task EmployeeCanStartAndReplaceOnlyTheirOwnSigningChallenge()
    {
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/SshKeys")).StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        var first = await PostForm("/SshKeys/Register", new() { ["name"] = "laptop", ["publicKey"] = PublicKey + " employee@laptop" }, "/SshKeys");
        AssertRedirect(first, "/SshKeys");
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var old = (await keys.ListAsync(userId)).Single();
        Assert.AreEqual(PublicKey, old.PublicKey);
        var html = await Http.GetStringAsync("/SshKeys");
        StringAssert.Contains(html, PersonalSshKeys.Challenge(old.Id));
        StringAssert.Contains(html, old.Fingerprint);
        await Assert.ThrowsAsync<DmsException>(() => keys.RequireVerifiedAsync(userId, old.Id, old.Fingerprint, default));
        var newer = await keys.BeginAsync(userId, "replacement", PublicKey);
        Assert.AreNotEqual(old.Id, newer.Id);
        await Assert.ThrowsAsync<DmsException>(() => keys.VerifyAsync(userId, old.Id, "stale signature", default));
        await Assert.ThrowsAsync<DmsException>(() => keys.VerifyAsync("another employee", newer.Id, "foreign signature", default));
        await Assert.ThrowsAsync<DmsException>(() => keys.BeginAsync(userId, "invalid", "command=\"echo\" " + PublicKey));
    }

    [TestMethod]
    public async Task VerifiedSelectionIsBoundToOwnerRegistrationAndFingerprintAndRevocationFreesOneSlot()
    {
        var (email, _) = await RegisterAndLoginAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var user = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var active = Enumerable.Range(1, 3).Select(i => new PersonalSshKey
        {
            UserId = user, Slot = i, Name = $"key-{i}", PublicKey = PublicKey,
            Fingerprint = $"fixture-{i}", VerifiedAt = DateTime.UtcNow
        }).ToList();
        db.AddRange(active);
        await db.SaveChangesAsync();
        var first = active[0];
        Assert.AreEqual(first.Id, (await keys.RequireVerifiedAsync(user, first.Id, first.Fingerprint, default)).Id);
        await Assert.ThrowsAsync<DmsException>(() => keys.RequireVerifiedAsync(user, first.Id, "wrong fingerprint", default));
        await Assert.ThrowsAsync<DmsException>(() => keys.RequireVerifiedAsync("other", first.Id, first.Fingerprint, default));
        await Assert.ThrowsAsync<DmsException>(() => keys.BeginAsync(user, "fourth", PublicKey));
        await Assert.ThrowsAsync<DmsException>(() => keys.RevokeAsync("other", first.Id));
        await keys.RevokeAsync(user, first.Id);
        await Assert.ThrowsAsync<DmsException>(() => keys.RequireVerifiedAsync(user, first.Id, first.Fingerprint, default));
        Assert.HasCount(2, await keys.ListAsync(user));
        Assert.IsNotNull((await db.PersonalSshKeys.FindAsync(first.Id))!.RevokedAt);
        Assert.IsTrue(await db.AuditEvents.AnyAsync(e => e.ResourceId == first.Id && e.ActorId == user));
    }
}
