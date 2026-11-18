using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class SshKeysViewModel : UiStackLayoutViewModel
{
    public SshKeysViewModel() => PageTitle = "SSH public keys";
    public required IReadOnlyList<PersonalSshKey> Keys { get; init; }
    public string? Notice { get; init; }
}
