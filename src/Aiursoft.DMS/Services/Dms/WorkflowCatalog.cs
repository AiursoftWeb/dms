using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Services.Dms;

// Reads the actual compiled registries; there is no second list of standard public operations.
public sealed class WorkflowCatalog(MachineWorkflowRegistry machines, ClusterWorkflowRegistry clusters)
{
    public IReadOnlyList<WorkflowDefinition> PublicDefinitions()
    {
        var definitions = machines.Definitions.Concat(clusters.Definitions).ToList();
        if (definitions.Select(d => d.Name).Distinct(StringComparer.Ordinal).Count() != definitions.Count)
            throw new InvalidOperationException("Workflow names must be unique across execution locations.");
        return definitions.Where(d => d.PublicEntry).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    public WorkflowDefinition? FindApprovable(string name, RiskLevel risk, string version) =>
        Find(name) is { PublicEntry: true } definition && definition.Risk == risk && definition.Version == version
            ? definition : null;

    public WorkflowDefinition? Find(string name)
    {
        var machine = machines.FindDefinition(name);
        var cluster = clusters.FindDefinition(name);
        if (machine != null && cluster != null) throw new InvalidOperationException("Workflow names must be unique across execution locations.");
        return machine ?? cluster;
    }
}
