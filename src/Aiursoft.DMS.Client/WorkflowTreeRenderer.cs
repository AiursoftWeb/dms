using Aiursoft.DMS.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public static class WorkflowTreeRenderer
{
    public static string Colorize(string text, RunState state, bool enabled)
    {
        var code = state switch
        {
            RunState.Succeeded => "32", RunState.Failed => "31",
            RunState.Running or RunState.Accepted or RunState.Offered => "34",
            RunState.Warning or RunState.NeedsVerification or RunState.CancelRequested => "33",
            _ => "37"
        };
        return enabled ? $"\u001b[{code}m{text}\u001b[0m" : text;
    }

    private sealed record Node(Guid Id, Guid? ParentId, string Definition, RunState State, WorkflowOrigin Origin = WorkflowOrigin.Manual,
        Guid? MachineId = null, string? MachineName = null, string? Result = null);

    public static IEnumerable<string> Render(JArray data, bool color)
    {
        var nodes = data.ToObject<List<Node>>()!;
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var children = nodes.Where(n => n.ParentId != null).ToLookup(n => n.ParentId!.Value);
        var visited = new HashSet<Guid>();
        // A subtree's first node may have a parent outside the selected result.
        var roots = nodes.Where(n => n.ParentId == null || !ids.Contains(n.ParentId.Value));
        foreach (var root in roots.Concat(nodes))
        {
            if (visited.Contains(root.Id)) continue;
            var stack = new Stack<(Node Node, string Prefix, string Connector, bool Last)>();
            stack.Push((root, string.Empty, string.Empty, true));
            while (stack.TryPop(out var item))
            {
                if (!visited.Add(item.Node.Id)) continue;
                var node = item.Node;
                var text = $"{node.Id}  {node.Definition}  [{node.State}]" + (node.Origin == WorkflowOrigin.Automatic ? " [Automatic]" : "");
                var descendants = children[node.Id].ToList();
                if (node.Definition == WorkflowNames.AgentUpgrade)
                    text += FormatUpgrade(node);
                else if (node.Definition == WorkflowNames.Batch && descendants.Count > 0 && descendants.All(n => n.Definition == WorkflowNames.AgentUpgrade))
                {
                    var succeeded = descendants.Count(n => n.State == RunState.Succeeded);
                    text += $"  {succeeded}/{descendants.Count} completed successfully";
                }
                yield return item.Prefix + item.Connector + Colorize(text, node.State, color);
                var prefix = item.Prefix + (item.Connector.Length == 0 ? string.Empty : item.Last ? "    " : "│   ");
                for (var i = descendants.Count - 1; i >= 0; i--)
                    stack.Push((descendants[i], prefix, i == descendants.Count - 1 ? "└── " : "├── ", i == descendants.Count - 1));
            }
        }
    }

    private static string FormatUpgrade(Node node)
    {
        var machine = DmsOutputWriter.TerminalText(node.MachineName ?? node.MachineId?.ToString());
        var label = $"  {machine}";
        if (string.IsNullOrWhiteSpace(node.Result)) return label;
        try
        {
            var result = JObject.Parse(node.Result);
            if (node.State == RunState.Succeeded && result.Value<string>("Version") is { } version)
            {
                var verified = DmsOutputWriter.TerminalText(version);
                if (result.Value<bool?>("AlreadyCurrent") == true) return $"{label} · Already current ({verified})";
                var previous = DmsOutputWriter.TerminalText(result.Value<string>("PreviousVersion"));
                return previous == verified ? $"{label} · Reconnected on {verified}" : $"{label} · Upgraded {previous} → {verified}";
            }
            if (result.Value<string>("Error") is { } error) return $"{label} · {DmsOutputWriter.TerminalText(error)}";
        }
        catch (JsonException) { }
        return label;
    }
}
