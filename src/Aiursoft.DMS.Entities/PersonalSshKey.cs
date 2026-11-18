using System.ComponentModel.DataAnnotations;

namespace Aiursoft.DMS.Entities;

public class PersonalSshKey
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string UserId { get; init; }
    [MaxLength(100)] public required string Name { get; init; }
    [MaxLength(8192)] public required string PublicKey { get; init; }
    [MaxLength(64)] public required string Fingerprint { get; init; }
    // Slot 0 is the employee's current challenge, 1–3 are verified keys, null is revoked.
    public int? Slot { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
