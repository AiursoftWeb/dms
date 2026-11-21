using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Aiursoft.DMS.Contracts;

public sealed record SshOpenInput(Guid KeyId, int Minutes = 30, bool Sudo = false, string? Fingerprint = null)
{
    public void Validate()
    {
        if (KeyId == Guid.Empty) throw new ArgumentException("Choose a verified personal SSH key.");
        if (Minutes is < 30 or > 270) throw new ArgumentException("SSH lease duration must be 30 to 270 minutes.");
        if (Fingerprint is { Length: > 80 }) throw new ArgumentException("Invalid SSH key fingerprint.");
    }
}

public sealed record SshLeaseAuthorization(Guid KeyId, string RequesterId, string PublicKey);
public sealed record SshRecoveryInput(Guid ExpectedLeaseId);

public sealed record SshLeaseCheckInput(bool CloseSshWhenNoLease = false);

public sealed record SshLeaseCheckResult(string Summary, bool? SshClosed);

public static class SshPublicKey
{
    public static (string Key, string Fingerprint) Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 8192 || input.Trim().Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("Enter one OpenSSH public key.");
        var parts = input.Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256" or
            "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521" or "sk-ssh-ed25519@openssh.com" or "sk-ecdsa-sha2-nistp256@openssh.com"))
            throw new ArgumentException("Unsupported OpenSSH public key type.");
        byte[] blob;
        try { blob = Convert.FromBase64String(parts[1]); }
        catch (FormatException) { throw new ArgumentException("Invalid public key encoding."); }
        var type = Encoding.ASCII.GetBytes(parts[0]);
        if (blob.Length <= 4 + type.Length || BinaryPrimitives.ReadUInt32BigEndian(blob) != type.Length
            || !blob.AsSpan(4, type.Length).SequenceEqual(type))
            throw new ArgumentException("Invalid public key data.");
        return ($"{parts[0]} {Convert.ToBase64String(blob)}", "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('='));
    }
}
