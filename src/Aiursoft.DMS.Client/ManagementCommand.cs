using System.CommandLine;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class ManagementCommand(string name, string action) : DmsCommandHandler
{
    protected override string Name => name;
    protected override string Description => "Inspect or configure Controller access. Mutations require Manage DMS access and are audited without creating a Lockbox.";
    private readonly Option<Guid> _cluster = new("--cluster-id") { Required = true };
    private readonly Option<Guid> _id = new("--id") { Description = "Creation ID; reuse it to retry the same cluster creation." };
    private readonly Option<string> _name = new("--name") { Required = true };
    private readonly Option<Guid> _machine = new("--machine-id") { Required = true };
    private readonly Option<int> _days = new("--days") { DefaultValueFactory = _ => 7 };

    protected override IEnumerable<Option> GetActionOptions()
    {
        if (action == "create") { yield return _id; yield return _name; }
        if (action == "rename") yield return _name;
        if (action is "rename" or "drop") yield return _cluster;
        if (action == "key") yield return _machine;
        if (action is "join-create" or "join-list" or "enrollments") yield return _cluster;
        if (action == "join-create") { yield return _name; yield return _days; }
        if (action == "join-revoke") { _id.Required = true; yield return _id; }
    }

    protected override Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context) => action switch
    {
        "identity" => client.CurrentUserAsync(),
        "clusters" => client.ListClustersAsync(),
        "access" => client.AccessAsync(),
        "create" => client.CreateClusterAsync(new CreateClusterInput(context.GetValue(_id) is var id && id != Guid.Empty ? id : Guid.NewGuid(), context.GetValue(_name) ?? "")),
        "rename" => client.RenameClusterAsync(context.GetValue(_cluster), context.GetValue(_name) ?? ""),
        "drop" => client.DropClusterAsync(context.GetValue(_cluster)),
        "key" => client.IssueAgentKeyAsync(context.GetValue(_machine)),
        "join-create" => client.IssueJoinKeyAsync(new JoinKeyInput(context.GetValue(_cluster), context.GetValue(_name) ?? "", context.GetValue(_days))),
        "join-list" => client.JoinKeysAsync(context.GetValue(_cluster)),
        "join-revoke" => client.RevokeJoinKeyAsync(context.GetValue(_id)),
        "enrollments" => client.EnrollmentsAsync(context.GetValue(_cluster)),
        _ => throw new InvalidOperationException("Unknown management command.")
    };
}
