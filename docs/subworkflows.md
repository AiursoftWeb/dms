# Typed subworkflows

For a complete definition, registration, SDK and CLI walkthrough, see [Adding a standard workflow](authoring-workflows.md).

Standard workflow calls return `Task<TResult>`. Business code uses `await`, typed input and results, and ordinary `try/catch`; it does not handle WebSocket messages or parse trace output.

A Cluster workflow can call another registered Cluster workflow or a registered Machine workflow on an approved machine. A Machine workflow can call Machine workflows on its own Agent. Machine contexts have no inventory database or remote-machine call API.

```csharp
public sealed class ReadAgentInstallations : ClusterWorkflow<EmptyInput, List<Guid>>
{
    public override async Task<List<Guid>> ExecuteAsync(
        WorkflowExecutionContext context, EmptyInput input, CancellationToken token)
    {
        var installations = new List<Guid>();
        foreach (var machine in await context.GetApprovedMachinesAsync(token))
        {
            var status = await context.CallMachineAsync(
                MachineWorkflows.AgentStatus, machine, new EmptyInput(), token);
            installations.Add(status.InstallationId);
        }
        return installations;
    }
}
```

Register a compiled Cluster definition at application startup with `ClusterWorkflowRegistry.Register(definition, () => new ReadAgentInstallations())`. A definition declares input and result types. Register compiled Machine definitions and their factories in `MachineWorkflowRegistry`; the Agent uses this registry for dispatch. Typed Machine classes can derive from `MachineWorkflow<TInput, TResult>`. Machine execution environments supply installation and Agent status information for local typed calls. Public operation validation and risk come from the registered shared definition. Every definition requires an explicit risk. PublicEntry defaults to false, keeping the definition internal regardless of its factory registration. The parent approval authorizes child calls even when a child has a higher risk; there is no second permission check. The internal process workflow is callable by compiled Machine code and is never a remote dispatch target.

Each call creates a separate workflow ID and preserves its root, parent and Lockbox IDs. Remote input and definition version belong to that instance and are persisted in the Controller database, offer and Agent journal. Repeated offers for the same ID must retain identical input and version. Local Machine calls report their own input and parent IDs. `operation get-workflow` and `operation get-callstack` expose the resulting tree through the existing authorized, audited query paths.

The independent Lockbox approval authorizes the entire call tree. Its execution root is created only after approval. Child calls do not create additional approval requests, but they cannot expand the frozen target list, use a disabled machine or turn a Machine workflow into a Cluster workflow. A machine added after submission is not included in `GetApprovedMachinesAsync`.

## Results and failures

A confirmed failed child throws `ChildWorkflowException`, carrying its workflow ID, definition, machine ID when remote, and final state. A parent can catch it and continue; the child stays failed in the tree. Ordinary Cluster completion follows its own code, so a handled failed child does not automatically fail its parent. Call `WarningAsync` when a known incomplete result should finish with a warning. Batch control workflows retain their own aggregation rules.

An unknown execution outcome raises `WorkflowNeedsVerificationException` and makes the parent need verification. Catching the exception does not turn an unknown outcome into success. Late child results are retained for investigation and do not automatically succeed a parent that already requires verification. This is distinct from a known execution failure.

Cancellation is requested through the existing approved kill operation. A cancelled parent cannot create additional children. An execution cancellation request remains visible until execution actually confirms stopping; cancelling a local wait is not evidence that a remote side effect stopped or was rolled back. Pass the workflow cancellation token to calls and asynchronous work.

## Scheduling and recovery

Ordinary Cluster code executes outside `ControllerGate`. Inventory work uses `WithInventoryAsync` for short database operations with an independent scope; do not wait for a child inside its database callback, retain its DbContext or entities, or return an unevaluated query. Trace messages use the existing durable spool and ClickHouse archive; trace bodies are not stored in the inventory database.

Live continuations belong to the current Controller process. This implementation does not serialize or replay a C# stack. On restart, queued work can resume; started ordinary parents whose continuations were lost need verification and are not executed again. Started Machine tasks preserve the existing journal/ACK and installation-identity recovery rules. Already persisted queued children can resume without recreating their calls. Restarting an Agent never automatically repeats a task recorded as started with an unknown outcome.

Use the typed Cluster implementation of `cluster.get-machines` as a built-in example. The two-Agent integration example additionally exercises nested Cluster calls, handled and unhandled failures, independent child parameters and IDs, frozen targets, cancellation, and unknown outcomes. No new public workflow command is added by this framework change.

## Approval handoff

Approval commits the Lockbox decision, queued root, execution snapshot and audit outbox together. All children use their root ID to inherit that snapshot. The Controller uses its frozen definition, version, inputs and machine IDs for dispatch and checks current machine availability. Artifact transport also checks the calling Agent, task state, owner and artifact metadata. Completing, failing or stopping a workflow leaves approval history and other pending requests intact. An Agent's built-in schedule has its own system origin and creates no human approval snapshot.
