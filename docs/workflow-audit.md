# Structured workflow investigation archive

[Workflow authoring](authoring-workflows.md) · [CLI and Agent](cli-and-agent.md)

DMS archives structured approval and execution facts to `DmsWorkflowAudit` in the configured `Dms:Clickhouse` database. The existing `DmsWorkflowTrace` table continues to hold trace text. No new service or separate connection configuration is needed. The CLI and Web investigation pages query these facts through the authenticated investigation API.

## Events and attribution

The archive appends `LockboxSubmitted`, `LockboxApproved`, `LockboxDenied`, `WorkflowCreated` and `WorkflowStateChanged` events. Every workflow instance, including internal subworkflows, retains its own ID, root, direct parent, Lockbox, name/version and execution location. Machine events also include the machine and the installation identity known at capture time. Cluster steps have no fabricated machine identity.

`RequesterId` is the human/API Key owner who requested execution. `ApproverId` identifies the reviewer; automatic approval is explicit and has no human reviewer. Subworkflows inherit their Lockbox context without creating new approval events. `RequestedDefinition`, `RequestedVersion` and `RequestedRisk` identify the original Lockbox request separately from each instance's own definition/version. A denied request therefore remains identifiable even though it creates no Workflow instance. Approval context is a snapshot when the event is saved; pending events are not rewritten after approval. Unavailable context is null rather than an invented identity.

States retain the existing meanings, including Warning, CancelRequested, Cancelled and NeedsVerification. A cancellation request is not confirmed termination. A NeedsVerification event is not a failed or successful execution; later confirmed information can add another event. No workflow is replayed to fill an archive gap.

Automatic approval records submission, approval and root creation in one transaction. The submission fact describes the pending request, with no Workflow ID or reviewer; the approval fact links the newly created execution root. Manual submission and denial have no Workflow/root ID or execution location. Approved non-execution requests may also have no Workflow ID. Workflow creation records its state at the first committed save. An uncommitted in-memory transition is not an archived fact. Repeated heartbeats, repeated offers and saves without a state change produce no state-change event.

`OccurredAt` uses the available creation, initial start, completion or decision time; other transitions use Controller observation time. In particular, a Running report after NeedsVerification does not reuse the original start time as the new event time. `RecordedAt` is the Controller capture time. Times are UTC, but Agent clocks are not assumed to be synchronized. `ControllerSequence` orders database capture within this Controller database; it is not a global clock across independent installations.

## Durable delivery

The shared EF save boundary captures facts into a relational `WorkflowAuditOutbox` with the business changes in the same transaction. The outbox contains only an immutable structured-metadata snapshot and a stable EventId. Workflow authors do not write audit calls or delivery code. Direct bulk SQL updates to workflow/approval state bypass tracked saves and are unsupported.

The existing background flush loop handles structured events and trace text independently. It sends up to 100 structured events per pass, then removes a pending row only after ClickHouse confirms the insert. A crash after insertion can repeat the physical insert with the same EventId and metadata. The table uses ReplacingMergeTree; logical investigation queries must use `FINAL` (or equivalent EventId deduplication). Physical insertion is at least once, not an exactly-once network transaction.

When ClickHouse is unavailable or not configured, pending events remain in the business database. Delivery failures and backlog counts are logged; approval does not require a live ClickHouse connection. If the business transaction cannot persist, neither its state change nor its audit event is confirmed. Agents retain unacknowledged events. This does not roll back operations already issued to machines.

No automatic expiry deletes undelivered rows. Disk capacity is finite; a prolonged outage can exhaust business storage and requires restoring archive delivery or storage. Restore the database and its pending outbox together; do not recreate pending events with new IDs. There is no historical backfill of operations performed before this feature was installed, and no extra retention or quota policy in this phase.

Only IDs, names/versions, locations, risks, decisions, states and timestamps enter the structured table. Credentials, hashes, input/parameters, results, trace bodies and free-text reasons are excluded. Follow the Lockbox/workflow IDs through existing authorized tools for that context. The relational database retains business metadata and a temporary delivery queue; large trace bodies remain outside it.

## Operator investigation example

Authorized database operators can inspect this instance directly:

```sql
SELECT RecordedAt, OccurredAt, ControllerSequence, Kind, MachineId,
       RequesterId, ApproverId, AutomaticApproval, RequestedDefinition, RequestedVersion, Definition, Version,
       PreviousState, State, WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId
FROM DmsWorkflowAudit FINAL
ORDER BY RecordedAt, ControllerSequence;
```

Direct database access requires its own authorization. This example does not expose an arbitrary-SQL DMS command. The investigation endpoint uses the global Read DMS operations permission.

## Authorized investigation queries

`GET /api/v1/dms/audit-events` and `dms operation query-audit` read archived facts directly. They do not create a Lockbox or workflow. A personal API Key and Read DMS operations permission are required; Agent keys cannot use this management endpoint. Queries use current global read permission, including revocations, and authorized query attempts are recorded in the existing access audit.

```sh
dms operation query-audit --state Failed

dms operation query-audit \
  --from 2026-11-14T00:00:00Z --until 2026-11-15T00:00:00Z \
  --requested-definition cluster.disk-usage --output-format json

dms operation query-audit \
  --machine-id "$MACHINE" --output-format csv
```

Filters are exact matches: `--requester-id`, `--approver-id`, `--machine-id`, `--workflow-id`, `--root-workflow-id`, `--lockbox-id`, `--definition` (the individual instance), `--requested-definition` (the original operation), `--kind`, `--state` and `--automatic-approval`. An event's State is its state at capture time, not the instance's current state. Approval events have no execution State. Use Kind to find approval/denial facts, then follow their WorkflowId or LockboxId through existing authorized diagnostics.

Time filtering uses Controller `RecordedAt`, with an inclusive `--from` and exclusive `--until`. Use ISO 8601 values with an explicit UTC offset. Defaults select the last 24 hours; a query can span at most 31 days. This limits each query, not archive retention. Select an older window to investigate older events. Responses include the normalized Query, Entries and HasNextPage. Each Entry has EventId, ControllerSequence and the structured Fact. The default table summarizes the event and retains the full LockboxId and optional WorkflowId; JSON retains all structured fields, and CSV flattens each Fact into one record, including attribution and machine IDs. Empty CSV pages still have headers.

`--page` is zero based, with 50 events per page. Ordering is RecordedAt, ControllerSequence and EventId descending after `FINAL` deduplication. Preserve the returned From and Until when requesting another page. These bounds stabilize the requested window; delivery of previously captured events can still change page boundaries. This is an eventually consistent archive, not a database snapshot. Recent changes may still be awaiting delivery. ClickHouse being disabled or unavailable returns an explicit 503, never a fabricated empty success; queries do not merge the pending queue into the archive.

The Web **Investigation** page uses the same authorization, filters and pagination, with links to workflow details, machine history and approval context. Time labels and values use UTC. No arbitrary SQL or output-body search or live workflow control is exposed by this query feature.

## Open history queries

`GET /api/v1/dms/lockbox-history` and `GET /api/v1/dms/workflow-history` expose the same archived facts to **every authenticated human key** without the Read DMS operations permission; Agent keys cannot use them. These endpoints return the same deliberately limited facts as the archive — never parameters, results, free-text reasons or credentials — so no approval flow or extra permission is needed to answer questions such as what a person requested recently, what happened on a machine, or how often an operation failed. Query attempts are recorded in the existing access audit. The permission-gated `audit-events` endpoint and Web Investigation page remain unchanged for full investigation use.

```sh
dms lockbox history --requester alice
dms lockbox history --machine web-01 --decision Approved
dms workflow history --definition machine.frps.deploy --state Failed
dms workflow history --root-workflow-id <UUID> --output-format csv
```

`lockbox history` returns only Lockbox lifecycle events (submitted, approved, denied, withdrawn); `workflow history` returns only workflow creation and state-change events. Filters are exact matches: `--requester` and `--approver` accept a username, display name or user ID; `--machine` accepts a machine name or machine ID; the Controller resolves readable names before querying the archive and rejects ambiguous or unknown names. The remaining filters mirror the authorized query: `--definition`, `--risk`, `--decision`, `--automatic-approval`, `--state`, `--origin`, `--trigger`, `--lockbox-id`, `--workflow-id` and `--root-workflow-id`. Time filtering uses the inclusive `--from` and exclusive `--until`, defaults to the last 7 days and can span at most 31 days. Pagination, ordering, eventual consistency and the explicit 503 on an unavailable archive behave exactly like the authorized query. Each entry additionally carries the resolved requester, approver and machine names; the default table shows them as readable columns, JSON and CSV include both names and stored IDs.

## Independent Lockbox upgrade

The Controller owns approvals as relational Lockbox entities. New requests never create a `lockbox` Workflow. `LockboxId` is exposed as `lb/<UUID>` by CLI, SDK and Web; the ClickHouse column continues storing the underlying UUID for historical query consistency. `WorkflowId`, `RootWorkflowId` and `Location` are nullable for approval facts. A failed execution does not change an Approved decision.

Startup upgrades the ClickHouse schema using the existing verified copy-and-exchange procedure, changing the sort key to `(LockboxId, EventId)` so events without a Workflow can be indexed. All old event IDs, values and associations are preserved. Stop the old Controller before upgrading; do not run both versions against the same database. The SQL migration retains historical approval wrapper IDs as `LegacyApprovalWorkflowId` and associates `WorkflowId` with the old actual operation. Old callstack/trace links remain valid; new trees contain execution nodes only. Historical pending wrappers remain historical records and are not reused when approving a pending request. Database downgrade is intentionally refused because it would require inventing approval workflows for new requests.

## Built-in automatic workflows

Agent-local compiled cron schedules enter the same archive. Their `Origin` is `Automatic`, while `Trigger` distinguishes `Cron`, `AgentStartup` and `Subworkflow`. `ScheduleId`, `ScheduleExpression` and `ScheduledAt` identify the plan and its UTC minute. Root and child Workflow IDs are ordinary investigation links. `LockboxId`, requester, approver and approval decision are absent: deployment of the reviewed built-in schedule is its authorization. Human requests, including automatically approved requests, retain `Origin = Manual`.

Use `dms operation query-audit --origin Automatic --output-format table` to find automatic runs; existing machine, definition and time filters still apply. `workflow callstack` labels automatic nodes, and `workflow status` includes origin and plan details. Trace rows also retain origin, trigger and schedule ID. Web workflow history and investigation can filter Manual/Automatic. Offline events remain in the Agent journal until Controller acknowledgment; the Controller's durable trace spool and audit outbox then deliver to ClickHouse with stable event IDs and deduplicated reads.

The upgrade migrates the investigation table's Lockbox reference to nullable and preserves existing approval and execution facts. Stop the old Controller before migrating, retain the normal database/ClickHouse backup and start the new Controller before upgrading Agents. New origin fields default historical facts to Manual; child investigation facts are identified as Subworkflow. Automatic workflow rows cannot be represented by the old required-Lockbox schema, so rolling back after accepting them requires restoring the coordinated pre-upgrade backup.

A requester withdrawal produces one `LockboxWithdrawn` event with the requester ID, decision time and Lockbox ID. Its workflow ID, approver and automatic-approval fields are empty. A pending request that passes its 4-hour expiry produces one `LockboxExpired` event the same way, with empty approver and automatic-approval fields. Approved workflow events obtain their approval context from the root's launch snapshot. Use `dms operation query-audit --kind LockboxWithdrawn` to investigate withdrawals. Expired Lockbox rows are deleted from the relational database 7 days after expiry; their audit events remain in the archive.
