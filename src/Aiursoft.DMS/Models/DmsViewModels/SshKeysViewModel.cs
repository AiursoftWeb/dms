using Aiursoft.DMS.Entities;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public class SshKeysViewModel : UiStackLayoutViewModel
{
    public SshKeysViewModel() => PageTitle = "SSH public keys";
    public required IReadOnlyList<PersonalSshKey> Keys { get; init; }
    public string? Notice { get; init; }
    public Guid? PendingId { get; init; }
}

public class AddSshKeyViewModel : UiStackLayoutViewModel
{
    public AddSshKeyViewModel() => PageTitle = "Add an SSH public key";
    public string? KeyName { get; init; }
    public string? PublicKey { get; init; }
    public string? Notice { get; init; }
    public bool VerificationUnavailable { get; init; }
}

public class SshKeyVerificationViewModel : UiStackLayoutViewModel
{
    public SshKeyVerificationViewModel() => PageTitle = "Verify your SSH public key";
    public required PersonalSshKey Key { get; init; }
    public bool SignatureInvalid { get; init; }
}
