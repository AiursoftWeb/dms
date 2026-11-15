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
    private readonly Option<string> _user = new("--user-id") { Required = true };
    private readonly Option<RiskLevel> _risk = new("--risk") { Required = true };
    private readonly Option<bool> _request = new("--can-request");
    private readonly Option<bool> _auto = new("--auto-approve");
    private readonly Option<bool> _approve = new("--can-approve");
    private readonly Option<bool> _enabled = new("--enabled") { Required = true };
    private readonly Option<Guid> _machine = new("--machine-id") { Required = true };

    protected override IEnumerable<Option> GetActionOptions()
    {
        if (action == "create") { yield return _id; yield return _name; }
        if (action == "rename") yield return _name;
        if (action is "rename" or "drop" or "grant") yield return _cluster;
        if (action is "grant" or "self") yield return _user;
        if (action == "grant") { yield return _risk; yield return _request; yield return _auto; yield return _approve; }
        if (action == "self") yield return _enabled;
        if (action == "key") yield return _machine;
    }

    protected override Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context) => action switch
    {
        "identity" => client.CurrentUserAsync(),
        "clusters" => client.ListClustersAsync(),
        "access" => client.AccessAsync(),
        "create" => client.CreateClusterAsync(new CreateClusterInput(context.GetValue(_id) is var id && id != Guid.Empty ? id : Guid.NewGuid(), context.GetValue(_name) ?? "")),
        "rename" => client.RenameClusterAsync(context.GetValue(_cluster), context.GetValue(_name) ?? ""),
        "drop" => client.DropClusterAsync(context.GetValue(_cluster)),
        "grant" => client.SetGrantAsync(new RiskGrant(context.GetValue(_cluster), context.GetValue(_user) ?? "", context.GetValue(_risk), context.GetValue(_request), context.GetValue(_auto), context.GetValue(_approve))),
        "self" => client.SetSelfApprovalAsync(new SelfApprovalInput(context.GetValue(_user) ?? "", context.GetValue(_enabled))),
        "key" => client.IssueAgentKeyAsync(context.GetValue(_machine)),
        _ => throw new InvalidOperationException("Unknown management command.")
    };
}
