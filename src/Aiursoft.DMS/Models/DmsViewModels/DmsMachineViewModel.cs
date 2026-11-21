using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class DmsMachineViewModel : UiStackLayoutViewModel
{
    public required Machine Machine { get; init; }
    public bool CanRotateAgentKey { get; init; }
}

public enum AgentKeyRotationError
{
    None,
    WrongName,
    StaleReview
}

public class AgentKeyRotationViewModel : UiStackLayoutViewModel
{
    public AgentKeyRotationViewModel() => PageTitle = "Replace the Agent connection key";
    public required Guid MachineId { get; init; }
    public required string MachineName { get; init; }
    public string? ConfirmationToken { get; init; }
    public AgentKeyRotationError Error { get; init; }
    public string? NewKey { get; init; }
}
