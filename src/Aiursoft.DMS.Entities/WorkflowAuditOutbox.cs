using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Entities;

public sealed class WorkflowAuditOutbox
{
    public long Id { get; set; }
    public Guid EventId { get; set; } = Guid.NewGuid();
    public string Metadata { get; set; } = string.Empty;
    public WorkflowAuditEntry Read() => JsonSerializer.Deserialize<WorkflowAuditEntry>(Metadata)
        ?? throw new InvalidDataException("Invalid workflow audit metadata.");
}
