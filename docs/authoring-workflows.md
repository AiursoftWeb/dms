# Adding a standard workflow

Define typed input and output, declare a shared definition, implement the business class and register its execution factory. A public CLI command is optional. Approval, machine selection, dispatch, instance IDs, call trees, trace transport, cancellation and recovery belong to the framework.

The runnable examples live in [WorkflowAuthoringExamples.cs](../tests/WorkflowAuthoringExamples.cs). [WorkflowAuthoringTests.cs](../tests/IntegrationTests/WorkflowAuthoringTests.cs) submits the example through the public API and typed SDK, obtains approval, runs two actual WebSocket Agents and checks results and the complete tree. The examples are compiled test code; the product does not expose them.

## 1. Declare the shared contract

Put the input, result and definition in `Aiursoft.DMS.Contracts` so the Controller, Agent and SDK can share them without referencing execution code. For example:

```csharp
public sealed record UnitInput(long Units, bool Fail = false)
{
    public void Validate()
    {
        if (Units is < 1 or > 1000)
            throw new ArgumentException("Units must be between 1 and 1000.");
    }
}
public sealed record UnitResult(long Units);

public static readonly MachineWorkflowDefinition<UnitInput, UnitResult> Unit = new(
    "example.unit", Version: "unit-1", ValidateInput: input => input.Validate());
```

`MachineWorkflowDefinition<TInput, TResult>` declares execution on an Agent. `ClusterWorkflowDefinition<TInput, TResult>` declares execution on the Controller. The definition owns its name, version, input/output types, input validation and optional public risk. Constructors require missing mandatory JSON arguments to fail; unknown input fields and incorrect JSON types fail as well. Write explicit business validation for allowed values, including null values where relevant. Validation must be deterministic and have no side effects; it also runs for typed subcalls.

Omitting `PublicRisk` makes a definition internal: a registered parent can call it, but even a user with all risk grants cannot submit it directly. Set an explicit risk, such as `PublicRisk: RiskLevel.Low`, to allow public Lockbox submissions after registering its implementation. Registering a class alone does not grant external access. A CLI entry is a separate optional choice.

A Machine definition always requires a frozen machine selection. A Cluster definition with `SelectMachines: true` accepts the same selection; its body can enumerate `GetApprovedMachinesAsync`. A Cluster definition without this flag rejects machine selection. It can still perform Controller-side work such as an inventory query.

Change the declared version when the approved implementation or input contract changes. Submitted requests retain their approved version. Version mismatch fails execution instead of silently using a different implementation. Definitions use the existing protocol version by default, but may declare their own version as the examples do; these are not Agent software versions.

## 2. Implement and register a Machine workflow

```csharp
public sealed class UnitWorkflow(UnitInput input)
    : MachineWorkflow<UnitInput, UnitResult>(AuthoringExamples.Unit, input)
{
    public override Task<UnitResult> ExecuteAsync(
        MachineWorkflowContext context, UnitInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (input.Fail)
            throw new InvalidOperationException("An intentional example failure.");
        return Task.FromResult(new UnitResult(input.Units));
    }
}
```

The generic base supplies the name, version and serialized input/result. Register the factory in `MachineWorkflowRegistry`, which is used by both Controller validation and Agent dispatch:

```csharp
registry.Register(AuthoringExamples.Unit, (input, _) => new UnitWorkflow(input));
```

Factories receive typed input and an `AgentEnvironment` for installation identity, Agent version and task counts. They construct a workflow; they must not start work or contact machines. Put execution in `ExecuteAsync`. Built-in examples register in the registry constructor; an application or test may register additional compiled factories before submitting work. Controller and Agent must have the same compiled Machine registrations.

A Machine workflow calls a local child directly:

```csharp
var unit = await context.CallAsync(
    AuthoringExamples.Unit, new UnitInput(input.Units), token);
total += unit.Units;
```

It has no remote-machine or inventory API. The child gets its own ID and version, and its parent is the calling Machine instance.

## 3. Implement and register a Cluster workflow

Derive from `ClusterWorkflow<TInput, TResult>` and implement `ExecuteAsync(WorkflowExecutionContext, TInput, CancellationToken)`. Register the shared definition and a factory in `ClusterWorkflowRegistry`:

```csharp
registry.Register(AuthoringExamples.Total, () => new TotalWorkflow());
```

A normal body can be a loop of typed calls:

```csharp
foreach (var machine in await context.GetApprovedMachinesAsync(token))
{
    var result = await context.CallMachineAsync(
        AuthoringExamples.Repeat, machine, input, token);
    total += result.Units;
}
```

A Cluster can also call another Cluster through `context.CallAsync(definition, input, token)`. Return your typed result when complete. Do not implement dispatch, poll a database or construct workflow IDs in business code.

For a confirmed child failure, ordinary `try/catch (ChildWorkflowException)` can recover, continue or record a warning. The exception provides the child's ID; the failed child remains red in the call tree even when the parent succeeds. The example intentionally probes a failure and then performs the successful work. For a genuinely incomplete aggregate, explicitly call `WarningAsync` and report what was omitted.

An unknown outcome is different: `WorkflowNeedsVerificationException` makes the parent need verification even if caught. Cancellation and unknown outcomes must not be converted into successful partial results. See [subworkflow semantics](subworkflows.md).

## 4. Submit through the SDK, optionally map a CLI command

A public definition can use the typed SDK overload:

```csharp
await client.SubmitAsync(AuthoringExamples.Total, clusterId,
    new RepeatInput(7, 3), "Review the example", allMachines: true);
```

Alternatively, `definition.Request(...)` creates an `OperationRequest` for a caller managing its own HTTP transport. `RequestId` may be supplied for retries of exactly the same request. A changed input must use a new request ID and obtain a new approval.

On the wire, `OperationRequest.Input` is a **JSON-encoded string**, matching the existing instance and task input representation. The SDK serializes the typed object; callers do not need to construct or parse this string. For example, a raw service-status request contains:

```json
{
  "Workflow": "machine.systemctl.status",
  "Input": "{\"Service\":\"example.service\"}",
  "ClusterId": "00000000-0000-0000-0000-000000000001",
  "MachineIds": ["00000000-0000-0000-0000-000000000002"],
  "RequestId": "00000000-0000-0000-0000-000000000003",
  "Reason": "Inspect the service"
}
```

Do not add fields to `OperationRequest` for a standard workflow. Controller lookup reads public risk, location and validation from the registered definition. Approval stores the submitted input, version and frozen machine IDs; dispatch forwards those values to the instance. Root approval covers its subcalls, which cannot expand the approved target set.

For an optional CLI entry, add a command path and map its options into the typed input through `DmsCommandApp.StandardCommand`. Existing `ServiceCommand` shows how to supply an option and input builder. The common handler supplies the request envelope and renders table/JSON/CSV. Do not add workflow-name cases to the common handler or implement a second approval path.

`cluster.add-machine` and approved cancellation remain dedicated control operations. Their control-specific fields are separate from standard input authoring.

## 5. Verify the workflow as a business operation

The compiled examples demonstrate these additions:

| Addition | Location |
| --- | --- |
| Typed inputs, results and definitions | Shared Contracts in product code; grouped in the example file for the test fixture |
| Machine and Cluster bodies | Workflows and Controller respectively |
| Execution factories | One registration per implementation in the corresponding registry |
| Business tests | Existing test project |
| Optional command | CLI path, options and typed input mapping |

No new request property, risk switch, machine whitelist, dispatch case, message field, database migration or scheduler change is needed for the example. The framework changes in this MR enable that experience; later workflow authors use the resulting API.

Test valid/invalid input, public risk, internal-only access, approved input and targets, typed results, known failure handling and parent/root IDs. Use `dms operation get-workflow --id ...`, `get-callstack` and `get-trace` to investigate each instance. The same commands apply to every new workflow.

Run the existing test project, including `WorkflowAuthoringTests`; use the repository's normal build and lint commands. The execution stack belongs to the running process: after a crash, queued work can resume, but started work with unknown results is not automatically replayed.

The [cluster disk usage operation](disk-usage.md) is a product example of the same extension points, with a typed Machine result, a sequential Cluster aggregate and an optional CLI entry.

## Discover registered public workflows

Public definitions registered by the factories automatically appear in the [standard workflow catalog](workflow-discovery.md). No separate catalog entry is required. Internal definitions (`PublicRisk = null`) remain available only to compiled subworkflow calls.
