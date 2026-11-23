using System.Net;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Tests.IntegrationTests;

[TestClass]
public class PersonalSshKeysTests : TestBase
{
    private const string PublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIHpyp+C8Ia2B6RVM1UCr5HG+EtGR2xz5OF05P9kPz84D";
    private const string SecurityPublicKey = "sk-ecdsa-sha2-nistp256@openssh.com AAAAInNrLWVjZHNhLXNoYTItbmlzdHAyNTZAb3BlbnNzaC5jb20AAAAIbmlzdHAyNTYAAABBBEbXA8Mv12Gy8kZvkkxQCtJ9anxg2L9298CzeLCbzq4AVyMoB/nhXWyufkNVxDFtw+rdLhph+g9ERo4VPaaoh1kAAAAJc3NoOnVsdHJh";

    [TestMethod]
    public async Task EmployeeCanResumeAndAbandonTheirOwnKeySetup()
    {
        Assert.AreEqual(HttpStatusCode.Found, (await Http.GetAsync("/SshKeys")).StatusCode);
        var (email, _) = await RegisterAndLoginAsync();
        var list = await Http.GetStringAsync("/SshKeys");
        StringAssert.Contains(list, "Add SSH public key");
        Assert.DoesNotContain("name=\"publicKey\"", list);
        var first = await PostForm("/SshKeys/Register", new() { ["name"] = "laptop", ["publicKey"] = PublicKey + " employee@laptop" }, "/SshKeys/Add");
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var old = await keys.GetPendingAsync(userId);
        Assert.IsNotNull(old);
        var verifyPath = $"/SshKeys/Verify/{old.Id}";
        AssertRedirect(first, verifyPath);
        Assert.AreEqual(PublicKey, old.PublicKey);
        Assert.IsEmpty(await keys.ListAsync(userId));
        var html = await Http.GetStringAsync(verifyPath);
        var decoded = WebUtility.HtmlDecode(html);
        StringAssert.Contains(html, PersonalSshKeys.Challenge(old.Id));
        StringAssert.Contains(html, old.Fingerprint);
        StringAssert.Contains(html, "id=\"yubikey-pane\"");
        StringAssert.Contains(html, "id=\"file-pane\"");
        StringAssert.Contains(decoded, PersonalSshKeys.SignWithAgentCommand(old.Id, old.PublicKey));
        StringAssert.Contains(decoded, PersonalSshKeys.SignWithFileCommand(old.Id, old.PublicKey));
        Assert.DoesNotContain("name=\"publicKey\"", html);
        AssertRedirect(await Http.GetAsync("/SshKeys/Add"), verifyPath);
        AssertRedirect(await PostForm("/SshKeys/Register", new() { ["name"] = "duplicate submit", ["publicKey"] = PublicKey }, verifyPath), verifyPath);
        await Assert.ThrowsAsync<DmsException>(() => keys.RequireVerifiedAsync(userId, old.Id, old.Fingerprint, default));
        Assert.AreEqual(0, await keys.AbandonAsync("another employee", old.Id));
        AssertRedirect(await PostForm("/SshKeys/Abandon", new() { ["id"] = old.Id.ToString() }, verifyPath), "/SshKeys");
        Assert.IsFalse(await db.PersonalSshKeys.AnyAsync(k => k.Id == old.Id));
        var newer = await keys.BeginAsync(userId, "replacement", PublicKey);
        Assert.AreNotEqual(old.Id, newer.Id);
        Assert.AreEqual(0, await keys.AbandonAsync(userId, old.Id));
        Assert.AreEqual(newer.Id, (await keys.GetPendingAsync(userId))!.Id);
        await Assert.ThrowsAsync<DmsException>(() => keys.VerifyAsync(userId, old.Id, "stale signature", default));
        await Assert.ThrowsAsync<DmsException>(() => keys.VerifyAsync("another employee", newer.Id, "foreign signature", default));
        await Assert.ThrowsAsync<DmsException>(() => keys.BeginAsync(userId, "invalid", "command=\"echo\" " + PublicKey));
    }

    [TestMethod]
    public async Task SecurityKeyVerificationSelectsYubikeyTabByDefault()
    {
        var (email, _) = await RegisterAndLoginAsync();
        var reg = await PostForm("/SshKeys/Register", new() { ["name"] = "yubikey", ["publicKey"] = SecurityPublicKey }, "/SshKeys/Add");
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var pending = await keys.GetPendingAsync(userId);
        Assert.IsNotNull(pending);
        var verifyPath = $"/SshKeys/Verify/{pending.Id}";
        AssertRedirect(reg, verifyPath);
        var html = await Http.GetStringAsync(verifyPath);
        var decoded = WebUtility.HtmlDecode(html);
        StringAssert.Contains(html, "id=\"yubikey-tab\" data-bs-toggle=\"tab\"");
        StringAssert.Contains(html, "id=\"yubikey-tab\" data-bs-toggle=\"tab\" data-bs-target=\"#yubikey-pane\" type=\"button\" role=\"tab\" aria-controls=\"yubikey-pane\" aria-selected=\"true\"");
        StringAssert.Contains(decoded, "<(ssh-add -L | grep 'sk-ecdsa')");
        StringAssert.Contains(decoded, "$HOME/.ssh/id_ecdsa_sk");
    }

    [TestMethod]
    public void VerifyCommandsAndKeyTypeAdaptation()
    {
        var id = Guid.NewGuid();
        var ed25519 = PublicKey;
        var rsa = "ssh-rsa AAAAB3NzaC1yc2EAAAADAQABAAABAQC...";
        var skEcdsa = SecurityPublicKey;
        var skEd25519 = "sk-ssh-ed25519@openssh.com AAAAGnNrLXNzaC1lZDI1NTE5QG9wZW5zc2guY29tAAAAI...";

        Assert.IsFalse(PersonalSshKeys.IsSecurityKey(ed25519));
        Assert.IsFalse(PersonalSshKeys.IsSecurityKey(rsa));
        Assert.IsTrue(PersonalSshKeys.IsSecurityKey(skEcdsa));
        Assert.IsTrue(PersonalSshKeys.IsSecurityKey(skEd25519));

        Assert.AreEqual("$HOME/.ssh/id_ed25519", PersonalSshKeys.DefaultPrivateKeyPath(ed25519));
        Assert.AreEqual("$HOME/.ssh/id_rsa", PersonalSshKeys.DefaultPrivateKeyPath(rsa));
        Assert.AreEqual("$HOME/.ssh/id_ecdsa_sk", PersonalSshKeys.DefaultPrivateKeyPath(skEcdsa));
        Assert.AreEqual("$HOME/.ssh/id_ed25519_sk", PersonalSshKeys.DefaultPrivateKeyPath(skEd25519));

        Assert.AreEqual("ssh-ed25519", PersonalSshKeys.SshAgentGrepPattern(ed25519));
        Assert.AreEqual("sk-ecdsa", PersonalSshKeys.SshAgentGrepPattern(skEcdsa));
        Assert.AreEqual("sk-ssh-ed25519", PersonalSshKeys.SshAgentGrepPattern(skEd25519));

        var skAgentCmd = PersonalSshKeys.SignWithAgentCommand(id, skEcdsa);
        StringAssert.Contains(skAgentCmd, "ssh-keygen -Y sign -n aiursoft-dms-key-registration");
        StringAssert.Contains(skAgentCmd, "<(ssh-add -L | grep 'sk-ecdsa')");

        var skFileCmd = PersonalSshKeys.SignWithFileCommand(id, skEcdsa);
        StringAssert.Contains(skFileCmd, "-f \"$HOME/.ssh/id_ecdsa_sk\"");
    }

    [TestMethod]
    public async Task ExpiredKeySetupIsUnusableAndCleanupKeepsVerifiedKeys()
    {
        var (email, _) = await RegisterAndLoginAsync();
        using var scope = Server!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
        var keys = scope.ServiceProvider.GetRequiredService<PersonalSshKeys>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        var elapsed = DateTime.UtcNow.AddMinutes(-11);
        var expired = new PersonalSshKey
        {
            UserId = userId, Name = "expired", PublicKey = PublicKey, Fingerprint = "fixture", CreatedAt = elapsed, Slot = 0
        };
        var verified = new PersonalSshKey
        {
            UserId = userId, Name = "verified", PublicKey = PublicKey, Fingerprint = "verified",
            CreatedAt = elapsed, Slot = 1, VerifiedAt = elapsed.AddMinutes(1)
        };
        db.AddRange(expired, verified);
        await db.SaveChangesAsync();
        var path = $"/SshKeys/Verify/{expired.Id}";
        AssertRedirect(await Http.GetAsync(path), "/SshKeys/Add");
        var add = await Http.GetStringAsync("/SshKeys/Add");
        StringAssert.Contains(add, "This verification has expired or was abandoned.");
        AssertRedirect(await PostForm(path, new() { ["id"] = expired.Id.ToString(), ["signature"] = "stale" }, "/SshKeys/Add"), "/SshKeys/Add");
        Assert.AreEqual(1, await keys.RemoveExpiredVerificationsAsync());
        Assert.IsFalse(await db.PersonalSshKeys.AnyAsync(k => k.Id == expired.Id));
        Assert.AreEqual(verified.Id, (await keys.ListAsync(userId)).Single().Id);
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
