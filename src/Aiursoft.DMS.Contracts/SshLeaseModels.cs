using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Aiursoft.DMS.Contracts;

public sealed record SshOpenInput(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] Guid KeyId = default,
    int Minutes = 30, bool Sudo = false, string? Fingerprint = null)
{
    public const string BaselineRequiredError =
        "Run the SSH disable workflow successfully before opening a temporary lease.";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SshApprovedKey[]? Keys { get; init; }

    public void Validate()
    {
        if (Minutes is < 30 or > 270) throw new ArgumentException("SSH lease duration must be 30 to 270 minutes.");
        if (Fingerprint != null && (KeyId == Guid.Empty || Fingerprint.Length is 0 or > 80))
            throw new ArgumentException("Invalid SSH key fingerprint.");
        // JSON arrays can contain null even when the element type is non-nullable.
        if (Keys != null && (KeyId != Guid.Empty || Keys.Length is < 1 or > SshPublicKey.MaxVerifiedPerUser ||
            Array.IndexOf(Keys, null!) >= 0 ||
            Keys.Any(key => key.Id == Guid.Empty || key.Fingerprint is not { Length: > 0 and <= 80 }) ||
            Keys.Select(key => key.Id).Distinct().Count() != Keys.Length ||
            Keys.Select(key => key.Fingerprint).Distinct(StringComparer.Ordinal).Count() != Keys.Length))
            throw new ArgumentException("Invalid approved SSH key selection.");
    }

    public SshApprovedKey[] ApprovedKeys() =>
        Keys ?? (KeyId != Guid.Empty && Fingerprint != null ? [new(KeyId, Fingerprint)] : []);
}

public sealed record SshApprovedKey(Guid Id, string Fingerprint);
public sealed record SshAuthorizedKey(Guid Id, string PublicKey);
public sealed record SshLeaseAuthorization(Guid KeyId, string RequesterId, string PublicKey)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SshAuthorizedKey[]? Keys { get; init; }
}
public sealed record SshRecoveryInput(Guid ExpectedLeaseId)
{
    public void Validate()
    {
        if (ExpectedLeaseId == Guid.Empty)
            throw new ArgumentException("SSH lease recovery requires an expected lease ID.");
    }
}

public sealed record SshLeaseCheckInput(bool CloseSshWhenNoLease = false)
{
    public bool ReportStatus { get; init; }
}

public sealed record SshHostObservation(DateTimeOffset ObservedAtUtc, bool? OrdinaryOpen, bool LeaseKnown,
    Guid? LeaseId = null, DateTimeOffset? ExpiresAtUtc = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BaselineReady { get; init; }
}

public sealed record SshLeaseCheckResult(string Summary, bool? SshClosed)
{
    public SshHostObservation? Observation { get; init; }
}

public static class SshPublicKey
{
    public const int MaxVerifiedPerUser = 3;

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
