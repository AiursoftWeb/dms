using System.Diagnostics;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

public sealed class PersonalSshKeys(TemplateDbContext db, ControllerGate gate, AccessService access)
{
    public const string SignatureNamespace = "aiursoft-dms-key-registration";
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromMinutes(10);
    public static string Challenge(Guid id) => $"{SignatureNamespace}/{id:D}";
    public static DateTime ExpiresAt(PersonalSshKey key) => key.CreatedAt + VerificationLifetime;

    public static string DefaultPrivateKeyPath(string publicKey)
    {
        var type = publicKey.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        return type switch
        {
            "sk-ecdsa-sha2-nistp256@openssh.com" => "$HOME/.ssh/id_ecdsa_sk",
            "sk-ssh-ed25519@openssh.com" => "$HOME/.ssh/id_ed25519_sk",
            "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" => "$HOME/.ssh/id_ecdsa",
            "ssh-rsa" => "$HOME/.ssh/id_rsa",
            _ => "$HOME/.ssh/id_ed25519"
        };
    }

    public static string SshAgentGrepPattern(string publicKey)
    {
        var type = publicKey.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0];
        return type switch
        {
            "sk-ecdsa-sha2-nistp256@openssh.com" => "sk-ecdsa",
            "sk-ssh-ed25519@openssh.com" => "sk-ssh-ed25519",
            "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" => "ecdsa-sha2",
            "ssh-rsa" => "ssh-rsa",
            _ => "ssh-ed25519"
        };
    }

    public static bool IsSecurityKey(string publicKey) =>
        publicKey.TrimStart().StartsWith("sk-", StringComparison.Ordinal);

    public static string SignWithFileCommand(Guid id, string publicKey) =>
        $"printf '%s' '{Challenge(id)}' | ssh-keygen -Y sign -n {SignatureNamespace} -f \"{DefaultPrivateKeyPath(publicKey)}\"";

    public static string SignWithAgentCommand(Guid id, string publicKey) =>
        $"printf '%s' '{Challenge(id)}' | ssh-keygen -Y sign -n {SignatureNamespace} -f <(ssh-add -L | grep '{SshAgentGrepPattern(publicKey)}')";

    public Task<List<PersonalSshKey>> ListAsync(string user) => VerifiedKeys(user).OrderBy(k => k.Slot).ToListAsync();

    private IQueryable<PersonalSshKey> VerifiedKeys(string user) => db.PersonalSshKeys.AsNoTracking()
        .Where(k => k.UserId == user && k.Slot > 0 && k.VerifiedAt != null && k.RevokedAt == null);

    public Task<PersonalSshKey?> GetPendingAsync(string user, Guid? id = null, CancellationToken token = default)
    {
        var cutoff = DateTime.UtcNow - VerificationLifetime;
        return db.PersonalSshKeys.AsNoTracking().SingleOrDefaultAsync(
            k => k.UserId == user && k.Slot == 0 && k.CreatedAt > cutoff && (id == null || k.Id == id), token);
    }

    public Task<PersonalSshKey> BeginAsync(string user, string name, string publicKey) => gate.RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new DmsException(400, "Enter a key name of up to 100 characters.");
        string canonical, fingerprint;
        try { (canonical, fingerprint) = SshPublicKey.Parse(publicKey); }
        catch (ArgumentException error) { throw new DmsException(400, error.Message); }
        var keys = await db.PersonalSshKeys.Where(k => k.UserId == user && k.Slot != null).ToListAsync();
        if (keys.Count(k => k.VerifiedAt != null) >= 3)
            throw new DmsException(409, "Revoke a key before adding another.");
        if (keys.Any(k => k.VerifiedAt != null && k.PublicKey == canonical))
            throw new DmsException(409, "This key is already registered.");
        var pending = keys.SingleOrDefault(k => k.Slot == 0);
        if (pending != null)
        {
            if (ExpiresAt(pending) > DateTime.UtcNow)
                throw new DmsException(409, "Finish or abandon the current verification before adding another key.");
            db.Remove(pending);
            await db.SaveChangesAsync();
        }
        var key = new PersonalSshKey { UserId = user, Name = name.Trim(), PublicKey = canonical, Fingerprint = fingerprint, Slot = 0 };
        db.Add(key);
        await db.SaveChangesAsync();
        return key;
    });

    public async Task VerifyAsync(string user, Guid id, string signature, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(signature) || signature.Length > 16384)
            throw new DmsException(400, "Enter an SSH signature.");
        var candidate = await GetPendingAsync(user, id, token);
        if (candidate == null)
            throw new DmsException(409, "Request a new signing challenge.");
        if (!await VerifySignatureAsync(candidate, signature, token))
            throw new DmsException(400, "The signature could not be verified for this challenge and key.");
        await gate.RunAsync(async () =>
        {
            var key = await db.PersonalSshKeys.SingleOrDefaultAsync(k => k.Id == id && k.UserId == user && k.Slot == 0, token);
            if (key == null || ExpiresAt(key) <= DateTime.UtcNow)
                throw new DmsException(409, "Request a new signing challenge.");
            var occupied = await db.PersonalSshKeys.Where(k => k.UserId == user && k.Slot > 0).Select(k => k.Slot).ToListAsync(token);
            var slot = Enumerable.Range(1, 3).FirstOrDefault(s => !occupied.Contains(s));
            if (slot == 0) throw new DmsException(409, "Revoke a key before adding another.");
            key.Slot = slot;
            key.VerifiedAt = DateTime.UtcNow;
            access.Audit(user, "ssh-key.verify", id, key.Fingerprint);
            return await db.SaveChangesAsync(token);
        });
    }

    public Task<int> AbandonAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var key = await db.PersonalSshKeys.SingleOrDefaultAsync(k => k.UserId == user && k.Id == id && k.Slot == 0);
        if (key == null) return 0;
        db.Remove(key);
        return await db.SaveChangesAsync();
    });

    public Task<int> RemoveExpiredVerificationsAsync() => gate.RunAsync(async () =>
    {
        var cutoff = DateTime.UtcNow - VerificationLifetime;
        var expired = await db.PersonalSshKeys.Where(k => k.Slot == 0 && k.CreatedAt <= cutoff).ToListAsync();
        if (expired.Count == 0) return 0;
        db.PersonalSshKeys.RemoveRange(expired);
        return await db.SaveChangesAsync();
    });

    private static async Task<bool> VerifySignatureAsync(PersonalSshKey key, string signature, CancellationToken token)
    {
        var dir = Directory.CreateTempSubdirectory("dms-verify-");
        try
        {
            var signers = Path.Combine(dir.FullName, "signers");
            var proof = Path.Combine(dir.FullName, "signature");
            await File.WriteAllTextAsync(signers, $"employee {key.PublicKey}\n", token);
            await File.WriteAllTextAsync(proof, signature.Trim() + "\n", token);
            var start = new ProcessStartInfo("/usr/bin/ssh-keygen")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                ArgumentList = { "-Y", "verify", "-f", signers, "-I", "employee", "-n", SignatureNamespace, "-s", proof }
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start ssh-keygen.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.StandardInput.WriteAsync(Challenge(key.Id));
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
                return process.ExitCode == 0;
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
        finally { dir.Delete(true); }
    }

    public Task<int> RevokeAsync(string user, Guid id) => gate.RunAsync(async () =>
    {
        var key = await db.PersonalSshKeys.SingleOrDefaultAsync(k => k.UserId == user && k.Id == id && k.Slot > 0);
        if (key == null) throw new DmsException(404, "Verified key not found.");
        key.Slot = null;
        key.RevokedAt = DateTime.UtcNow;
        access.Audit(user, "ssh-key.revoke", id, key.Fingerprint);
        return await db.SaveChangesAsync();
    });

    public async Task<PersonalSshKey> RequireVerifiedAsync(string user, Guid id, string? fingerprint, CancellationToken token)
    {
        var key = await VerifiedKeys(user).SingleOrDefaultAsync(k => k.Id == id, token);
        if (key == null || (fingerprint != null && !string.Equals(key.Fingerprint, fingerprint, StringComparison.Ordinal)))
            throw new DmsException(409, "The selected key is unavailable. Submit a new request.");
        return key;
    }
}
