using System.CommandLine;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class ManagementCommand(string name, string action) : DmsCommandHandler
{
    protected override string Name => name;
    protected override string Description => "Inspect or configure Controller access. Mutations require Manage DMS access and are audited without creating a Lockbox.";
    private readonly Option<Guid> _id = new("--id") { Description = "Join key ID." };
    private readonly Option<string> _name = new("--name") { Required = true };
    private readonly Option<Guid> _machine = new("--machine-id") { Required = true };
    private readonly Option<int> _days = new("--days") { DefaultValueFactory = _ => 7 };

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
