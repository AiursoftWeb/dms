using Aiursoft.DMS.Contracts;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public enum MachineConnection { Online, Offline, NeverConnected, Disabled }
public enum SshLeaseReadiness { Unknown, Ready, NeedsSetup, InUse, OrdinaryOpen, AgentDisconnected }

public sealed record DmsSshLeaseFailure(string MachineName, Guid? MachineId);

public sealed record DmsMachineSshRow(Guid MachineId, SshHostObservation? Observation = null,
    string? Requester = null, Guid? LockboxId = null);

public sealed class DmsMachineRow
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string AgentVersion { get; init; }
    public DateTime? LastSeen { get; init; }
    public MachineConnection Connection { get; init; }
}

public sealed class DmsMachinesViewModel : UiStackLayoutViewModel
{
    public required IReadOnlyList<DmsMachineRow> Machines { get; init; }
    public IReadOnlyDictionary<Guid, DmsMachineSshRow> SshStates { get; init; } = new Dictionary<Guid, DmsMachineSshRow>();
    public required string Search { get; init; }
    public MachineConnection? Connection { get; init; }
    public int Page { get; init; }
    public bool HasNextPage { get; init; }
}
