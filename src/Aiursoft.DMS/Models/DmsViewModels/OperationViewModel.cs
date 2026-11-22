using System.Text.Json;
using Aiursoft.DMS.Entities;
using Aiursoft.DMS.Services.Dms;
using Aiursoft.UiStack.Layout;

namespace Aiursoft.DMS.Models.DmsViewModels;

public sealed class OperationViewModel : UiStackLayoutViewModel
{
    public required WorkflowRun Run { get; init; }
    public required IReadOnlyList<WorkflowRun> Steps { get; init; }
    public required IReadOnlyList<Guid> Targets { get; init; }
    public string? RequestedDefinition { get; init; }
    public required IReadOnlyDictionary<Guid, string> MachineNames { get; init; }
    public IReadOnlyList<TraceEntry> Trace { get; set; } = [];
    public bool TraceUnavailable { get; set; }
    public int TracePage { get; init; }
    public bool HasNextTracePage { get; set; }
    public string? ResultSummary => ReadSummary(Run.Result);
    public string? MachineName(Guid? id) => id.HasValue ? MachineNames.GetValueOrDefault(id.Value, id.Value.ToString()) : null;

    public IEnumerable<(WorkflowRun Step, int Depth)> OrderedSteps()
    {
        var children = Steps.ToLookup(step => step.ParentId);
        var seen = new HashSet<Guid>();
        var pending = new Stack<(WorkflowRun Step, int Depth)>();
        pending.Push((Run, 0));
        while (pending.TryPop(out var node))
        {
            if (!seen.Add(node.Step.Id)) continue;
            yield return node;
            foreach (var child in children[node.Step.Id].Reverse())
                pending.Push((child, node.Depth + 1));
        }
    }

    private static string? ReadSummary(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return null;
        try
        {
            using var json = JsonDocument.Parse(result);
            if (json.RootElement.ValueKind == JsonValueKind.String)
                return Shorten(json.RootElement.GetString());
            if (json.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "Error", "StandardError", "Stderr", "Summary", "Message", "Reason" })
            {
                foreach (var field in json.RootElement.EnumerateObject())
                {
                    if (!field.Name.Equals(key, StringComparison.OrdinalIgnoreCase) || field.Value.ValueKind != JsonValueKind.String) continue;
                    var summary = Shorten(field.Value.GetString());
                    if (summary != null) return summary;
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return Shorten(result);
        }
    }

    private static string? Shorten(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > 600 ? text[..600] + "…" : text;
    }
}
