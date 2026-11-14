using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Services.Dms;

// Reads the actual compiled registries; there is no second list of standard public operations.
public sealed class WorkflowCatalog(MachineWorkflowRegistry machines, ClusterWorkflowRegistry clusters)
{
    public WorkflowDefinition? Find(string name)
    {
        var machine = machines.FindDefinition(name);
        var cluster = clusters.FindDefinition(name);
        if (machine != null && cluster != null) throw new InvalidOperationException("Workflow names must be unique across execution locations.");
        return machine ?? cluster;
    }
}
