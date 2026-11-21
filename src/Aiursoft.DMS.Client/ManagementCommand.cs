using System.CommandLine;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class ManagementCommand(string name, string action) : DmsCommandHandler
{
    protected override string Name => name;
    protected override string Description => action switch
    {
        "identity" => "Show the signed-in account, its roles, and its effective permissions.",
        "access" => "List machines and whether their Agent access is disabled.",
        "key" => "Replace the Agent key for an existing machine and invalidate its previous key.",
        "join-create" => "Create an expiring key for enrolling new Agents.",
        "join-list" => "List enrollment keys, their expiration dates, and revocation status.",
        "join-revoke" => "Revoke an enrollment key to prevent new Agents from using it.",
        "enrollments" => "List Agent enrollment requests and their approval status.",
        _ => throw new InvalidOperationException("Unknown management command.")
    };
    private readonly Option<Guid> _id = new("--id")
        { Description = "ID of the enrollment key from dms access list-join-keys." };
    private readonly Option<string> _name = new("--name")
        { Required = true, Description = "A label that identifies the enrollment key." };
    private readonly Option<Guid> _machine = new("--machine-id")
        { Required = true, Description = "ID of the registered machine whose Agent key will be replaced." };
    private readonly Option<int> _days = new("--days")
        { DefaultValueFactory = _ => 7, Description = "Days until the enrollment key expires (1 to 365)." };

    protected override IEnumerable<Option> GetActionOptions()
    {
        if (action == "key") yield return _machine;
        if (action == "join-create") { yield return _name; yield return _days; }
        if (action == "join-revoke") { _id.Required = true; yield return _id; }
    }

    protected override Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context) => action switch
    {
        "identity" => client.CurrentUserAsync(),
        "access" => client.AccessAsync(),
        "key" => client.IssueAgentKeyAsync(context.GetValue(_machine)),
        "join-create" => client.IssueJoinKeyAsync(new JoinKeyInput(context.GetValue(_name) ?? "", context.GetValue(_days))),
        "join-list" => client.JoinKeysAsync(),
        "join-revoke" => client.RevokeJoinKeyAsync(context.GetValue(_id)),
        "enrollments" => client.EnrollmentsAsync(),
        _ => throw new InvalidOperationException("Unknown management command.")
    };
}
