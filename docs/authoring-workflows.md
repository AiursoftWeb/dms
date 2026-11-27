# Adding a standard workflow

Define typed input and output, declare a shared definition, implement the business class and register its execution factory. A public CLI command is optional. Approval, machine selection, dispatch, instance IDs, call trees, trace transport, cancellation and recovery belong to the framework. Lockbox is an independent approval entity; submitting creates no Workflow until approval. A standard author writes only the business function, including ordinary awaited subworkflow calls, and never implements approval inside that function.

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

public static class AuthoringExamples
{
    [WorkflowRisk(RiskLevel.VeryHigh)]
    public static readonly MachineWorkflowDefinition<UnitInput, UnitResult> Unit = new(
        "example.unit", WorkflowMetadata.Risk(typeof(AuthoringExamples), nameof(Unit)),
        Version: "unit-1", ValidateInput: input => input.Validate());
}
```

`MachineWorkflowDefinition<TInput, TResult>` declares execution on an Agent. `ClusterWorkflowDefinition<TInput, TResult>` declares execution on the Controller. The definition owns its name, version, input/output types, input validation, mandatory risk and a separate public-entry flag. Constructors require missing mandatory JSON arguments to fail; unknown input fields and incorrect JSON types fail as well. Write explicit business validation for allowed values, including null values where relevant. Validation must be deterministic and have no side effects; it also runs for typed subcalls.

Declare exactly one `[WorkflowRisk]` on the shared definition field, using one of the six levels in the [workflow risk classification policy](workflow-risk.md). Read that attribute with `WorkflowMetadata.Risk`; do not repeat a risk literal in the constructor or implementation. Shared fields let Controller approval, discovery, CLI and Agent read the same value without moving machine execution code into Contracts. Missing or invalid risk declarations fail registration. Existing Machine and Cluster risks retain their values. `PublicEntry` defaults to `false`: a registered parent can call the definition, but even a user with all risk permissions cannot submit it directly. Set `PublicEntry: true` to allow public Lockbox submissions after registering its implementation. Registering a class alone does not grant external access. A CLI entry is a separate optional choice.

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

### Register machine workflows from a class library

Reference `Aiursoft.DMS.Workflows` from the class library and group its factories in one public method. The `AuthoringExamples` class registers two machine workflows this way. [Compiled example](../tests/WorkflowAuthoringExamples.cs)

```csharp
public static void RegisterMachines(MachineWorkflowRegistry registry)
{
    registry.Register(Unit, (input, _) => new UnitWorkflow(input));
    registry.Register(Repeat, (input, environment) => new RepeatWorkflow(input, environment.InstallationId));
}
```

Reference the library from both host projects. Each host calls `AuthoringExamples.RegisterMachines(registry)` once on its own registry. [Two-Agent integration test](../tests/IntegrationTests/WorkflowAuthoringTests.cs)

- **Controller** — In `Startup.ConfigureServices`, call the method in the factory that creates the `MachineWorkflowRegistry` singleton. The Controller uses that registry to validate Lockbox requests. [Controller startup](../src/Aiursoft.DMS/Startup.cs)
- **Agent** — In `AgentRuntime.RunAsync`, create a registry, call the method and pass that registry to the `AgentClient` constructor so the Agent can execute the workflows. [Agent startup](../src/Aiursoft.DMS.Agent/AgentRuntime.cs)

Add the next workflow's factory to the same method. To offer a CLI command for a public definition such as `Repeat`, add a `StandardCommand` mapping to the appropriate `DmsCommandApp` group. [CLI command groups](../src/Aiursoft.DMS.Client/DmsCommandApp.cs)

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
await client.SubmitAsync(AuthoringExamples.Total,
    new RepeatInput(7, 3), "Review the example", allMachines: true);
```

Alternatively, `definition.Request(...)` creates an `OperationRequest` for a caller managing its own HTTP transport. `RequestId` may be supplied for retries of exactly the same request. A changed input must use a new request ID and obtain a new approval.

On the wire, `OperationRequest.Input` is a **JSON-encoded string**, matching the existing instance and task input representation. The SDK serializes the typed object; callers do not need to construct or parse this string. For example, a raw service-status request contains:

```json
{
  "Workflow": "machine.systemctl.status",
  "Input": "{\"Service\":\"example.service\"}",
  "MachineIds": ["00000000-0000-0000-0000-000000000002"],
  "RequestId": "00000000-0000-0000-0000-000000000003",
  "Reason": "Inspect the service"
}
```

Do not add fields to `OperationRequest` for a standard workflow. Controller lookup reads public risk, location and validation from the registered definition. Approval stores the submitted input, version and frozen machine IDs; dispatch forwards those values to the instance. Root approval covers its subcalls, which cannot expand the approved target set.

The `machine networking` group discovers ordinary commands declared on public `OperationsWorkflows` fields. For example, `NetworkingDnsQuery` has `[WorkflowCommand("Resolve a hostname from the selected machine.")]`. Its existing ID supplies the path `dms machine networking dns-query`. The input record marks the `Host` constructor parameter with `[WorkflowOption("--host", "Hostname to resolve from the selected machine.")]`, which supplies the required `--host` option. `NetworkingRoutes` follows the same convention with `EmptyInput` and no business options.

For another operation in this group, declare its typed contract and command description, write the business method, and add one `registry.RegisterOperation(definition, method)` binding in `NetworkingOperations.Register`. The common CLI builds the typed input and applies the definition's validation. This convention supports a single public input constructor with zero or more explicitly annotated required string arguments. Use the existing `DmsCommandApp.StandardCommand` mapping for aliases, typed defaults or custom option parsing. Both paths use the usual Lockbox submission and output formats.

The CLI is distributed as the `Aiursoft.DMS.Client` NuGet tool, including its SDK and Contracts assemblies. When changing any of those bundled components, increment `Version` in `src/Aiursoft.DMS.Client/Aiursoft.DMS.Client.csproj` before merging. Published versions are immutable, and the shared CI skips duplicate versions. Version the CLI independently of the Controller and Agent packages.

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

Test valid/invalid input, public risk, internal-only access, approved input and targets, typed results, known failure handling and parent/root IDs. Use `dms workflow status ...`, `workflow callstack` and `workflow trace` to investigate each instance. The same commands apply to every new workflow.

Run the existing test project, including `WorkflowAuthoringTests`; use the repository's normal build and lint commands. The execution stack belongs to the running process: after a crash, queued work can resume, but started work with unknown results is not automatically replayed.

The [cluster disk usage operation](disk-usage.md) is a product example of the same extension points, with a typed Machine result, a sequential Cluster aggregate and an optional CLI entry.

## Discover registered public workflows

Public definitions registered by the factories automatically appear in the [standard workflow catalog](workflow-discovery.md). No separate catalog entry is required. Internal definitions (`PublicEntry = false`) remain available only to compiled subworkflow calls.

Structured approval and state history is [captured automatically](workflow-audit.md) at the shared save boundary. Standard workflow authors do not register an audit writer or add delivery calls.

In a Machine workflow, write diagnostic context with `await context.TraceAsync(message)` and use `await context.WarningAsync(message)` when a returned observation is incomplete. A Cluster workflow uses `TraceAsync(message, token)` and marks its warning state with `WarningAsync(token)`; trace the reason explicitly. Log the approved inspection scope, relevant limits, the observed result and any uncertainty. Explain partial results before throwing, while retaining the failure; a caught `ChildWorkflowException` provides the child ID to link to its native evidence. Do not place passwords, tokens, SQL text, script bodies or whole input/environment objects in diagnostic messages. Fixed process helpers report execution metrics automatically; `OperationWorkflow<TInput>` also traces the returned summary. Ordinary workflow classes should trace their own meaningful completion summary. See [trace storage and investigation](operations.md#submit-and-investigate) for delivery, pagination and child-trace behavior.

The [operations catalog](operations.md) uses the same typed definitions and registry. `OperationWorkflow<TInput>` adapts a compiled business function returning `OperationResult`; `OperationOutput.ReportAsync` writes bounded trace and marks incomplete observations as warnings. A normal class deriving from `MachineWorkflow<TInput, TResult>` remains equally supported. Diagnostic compositions call typed children, for example `context.CallAsync(OperationsWorkflows.DockerInspect, input, token)`.

For artifact inputs, implement `IArtifactOperationInput` so submission/approval validates the requester's immutable manifest. Bounded script/SQL source additionally implements `IAuditTextInput`, which retains its bytes for approval audit. The generic definition discovers these interfaces; new authors do not add workflow-name cases to the Controller. The Agent environment supplies task-bound artifact transport. Binary payloads belong there, while ordinary text/results use trace. A lifecycle operation that crosses a process/kernel restart requires a durable confirmation protocol, as the installed-Agent reboot workflow does.

## Add a built-in machine schedule

For a parameterless, repeatable local inspection or reconciliation, put a concrete class in `Aiursoft.DMS.Workflows` and declare its risk and UTC schedule:

```csharp
[WorkflowRisk(RiskLevel.Safe)]
[RunCron("*/5 * * * *", RunOnStartup = true)]
public sealed class ScheduledAgentStatusWorkflow : MachineWorkflow
{
    public override string Name => "machine.agent.scheduled-status";
    public override string Version => "1";

    public override async Task<string> ExecuteAsync(
        MachineWorkflowContext context, CancellationToken token)
    {
        await context.TraceAsync("Automatic Agent status inspection started.");
        var result = await context.CallAsync(MachineWorkflows.AgentStatus, new EmptyInput(), token);
        await context.TraceAsync("Automatic Agent status inspection completed; no machine configuration was changed.");
        return JsonSerializer.Serialize(result);
    }
}
```

This is the shipped read-only example in `ScheduledWorkflows.cs`. The registry scans only the compiled Workflows assembly, registers its factory and reads its attributes. No BackgroundService, CLI command, approval code or separate cron registration is needed. Normal typed subcalls and `try/catch (ChildWorkflowException)` work exactly as in manually requested work. Add meaningful trace even for a successful no-op. An uncaught failure remains a failed run and does not stop other schedules.

Cronos parses **five fields in UTC**; seconds and a machine-local time zone are not supported. `RunOnStartup` defaults to false. A scheduled class must have a public parameterless constructor and no business input (`Input == "{}"`). Invalid cron, missing risk, duplicate names and unsupported classes fail startup. Constructors must have no side effects. Dynamic plugin loading, user-submitted cron and scheduled Controller workflows are outside this stage.

Reviewed built-in schedules become active when the new Agent is deployed. Each run is authorized by that release and creates **no Lockbox**. Its origin is `Automatic`; its trigger is `Cron` or `AgentStartup`. Children inherit the origin and plan metadata and use `Subworkflow` as their trigger. A person's automatically approved request remains `Manual`. Human public entry still requires the usual risk permissions and Lockbox. The shipped example has no public entry.

Every execution has a normal Workflow ID. A disconnected Agent continues scheduling and persists the root, child events and trace locally. Reconnection replays them under the same IDs into Controller history and ClickHouse. Install the matching Controller before upgrading Agents; the Controller rejects undeclared or mismatched schedules and retains unacknowledged Agent events for diagnosis. Keep old definitions available until their offline backlog has been archived when changing a released schedule's name, expression or version.

Only one invocation of a plan runs on a machine at a time. The journal persists its UTC minute; repeated ticks, a restart in that minute or a backward clock adjustment cannot rerun it. Missed minutes are not replayed. `RunOnStartup` consumes the startup minute as well. Accepted but unstarted work resumes; started work with an unknown outcome becomes `NeedsVerification` and blocks further invocations of that plan until reconciled. Do not use this facility for non-repeatable destructive jobs. An Agent that is stopped runs no checks; there is no independent system timer.

Cancellation affects that invocation and its children, not the installed schedule. Agent upgrade drains active work; shutdown propagates cancellation through the same runtime. Continue to investigate with `workflow status`, `workflow callstack`, `workflow trace`, and `operation query-audit --origin Automatic`. Web history and investigation provide the same origin filter. `ScheduledWorkflowTests` and `ScheduledWorkflowIntegrationTests` cover declarations, offline execution, recovery, caught child failure and two real WebSocket Agents.
