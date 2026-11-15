using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Newtonsoft.Json;

namespace Aiursoft.DMS.Entities;

public sealed class JoinKey
{
    [Key] public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ClusterId { get; init; }
    [JsonIgnore, ForeignKey(nameof(ClusterId)), NotNull] public Cluster? Cluster { get; set; }
    [MaxLength(450)] public required string IssuerId { get; init; }
    [JsonIgnore, ForeignKey(nameof(IssuerId)), NotNull] public User? Issuer { get; set; }
    [MaxLength(100)] public required string Name { get; init; }
    [MaxLength(64)] public required string KeyHash { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public bool Revoked { get; set; }
}

public sealed class AgentEnrollment
{
    [Key] public Guid Id { get; init; }
    public required Guid JoinKeyId { get; init; }
    [JsonIgnore, ForeignKey(nameof(JoinKeyId)), NotNull] public JoinKey? JoinKey { get; set; }
    public required Guid LockboxId { get; init; }
    [JsonIgnore, ForeignKey(nameof(LockboxId)), NotNull] public LockboxRequest? Lockbox { get; set; }
    public Guid InstallationId { get; init; }
    [MaxLength(100)] public required string MachineName { get; init; }
    [MaxLength(64)] public required string ReceiptHash { get; init; }
    [MaxLength(64)] public required string AgentKeyHash { get; init; }
    /// <summary>Null until the approved registration workflow creates the machine.</summary>
    public required Guid? MachineId { get; set; }
    [JsonIgnore, ForeignKey(nameof(MachineId))] public Machine? Machine { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
