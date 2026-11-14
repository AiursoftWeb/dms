# Structured workflow investigation archive

[Workflow authoring](authoring-workflows.md) · [CLI and Agent](cli-and-agent.md)

DMS archives structured approval and execution facts to `DmsWorkflowAudit` in the configured `Dms:Clickhouse` database. The existing `DmsWorkflowTrace` table continues to hold trace text. No new service or separate connection configuration is needed. This phase supplies reliable archive ingestion; it does not add a public investigation-query API or new CLI/Web investigation page.

## Events and attribution

The archive appends `LockboxSubmitted`, `LockboxApproved`, `LockboxDenied`, `WorkflowCreated` and `WorkflowStateChanged` events. Every workflow instance, including internal subworkflows, retains its own ID, root, direct parent, Lockbox, cluster, name/version and execution location. Machine events also include the machine and the installation identity known at capture time. Cluster steps have no fabricated machine identity.

`RequesterId` is the human/API Key owner who requested execution. `ApproverId` identifies the reviewer; automatic approval is explicit and has no human reviewer. Subworkflows inherit their Lockbox context without creating new approval events. `RequestedDefinition`, `RequestedVersion` and `RequestedRisk` identify the original Lockbox request separately from each instance's own definition/version. A denied request therefore remains identifiable even though it creates no business child. Approval context is a snapshot when the event is saved; pending events are not rewritten after approval. Unavailable context is null rather than an invented identity.

States retain the existing meanings, including Warning, CancelRequested, Cancelled and NeedsVerification. A cancellation request is not confirmed termination. A NeedsVerification event is not a failed or successful execution; later confirmed information can add another event. No workflow is replayed to fill an archive gap.

Automatic approval can create submission and approval facts in one transaction. Both reflect the committed approval snapshot; the archive does not invent an intermediate persisted state. Workflow creation records its state at the first committed save. An uncommitted in-memory transition is not an archived fact. Repeated heartbeats, repeated offers and saves without a state change produce no state-change event.

`OccurredAt` uses the available creation, initial start, completion or decision time; other transitions use Controller observation time. In particular, a Running report after NeedsVerification does not reuse the original start time as the new event time. `RecordedAt` is the Controller capture time. Times are UTC, but Agent clocks are not assumed to be synchronized. `ControllerSequence` orders database capture within this Controller database; it is not a global clock across independent installations.

## Durable delivery

The shared EF save boundary captures facts into a relational `WorkflowAuditOutbox` with the business changes in the same transaction. The outbox contains only an immutable structured-metadata snapshot and a stable EventId. Workflow authors do not write audit calls or delivery code. Direct bulk SQL updates to workflow/approval state bypass tracked saves and are unsupported.

The existing background flush loop handles structured events and trace text independently. It sends up to 100 structured events per pass, then removes a pending row only after ClickHouse confirms the insert. A crash after insertion can repeat the physical insert with the same EventId and metadata. The table uses ReplacingMergeTree; logical investigation queries must use `FINAL` (or equivalent EventId deduplication). Physical insertion is at least once, not an exactly-once network transaction.

When ClickHouse is unavailable or not configured, pending events remain in the business database. Delivery failures and backlog counts are logged; approval does not require a live ClickHouse connection. If the business transaction cannot persist, neither its state change nor its audit event is confirmed. Agents retain unacknowledged events. This does not roll back operations already issued to machines.

No automatic expiry deletes undelivered rows. Disk capacity is finite; a prolonged outage can exhaust business storage and requires restoring archive delivery or storage. Restore the database and its pending outbox together; do not recreate pending events with new IDs. There is no historical backfill of operations performed before this feature was installed, and no extra retention or quota policy in this phase.

Only IDs, names/versions, locations, risks, decisions, states and timestamps enter the structured table. Credentials, hashes, input/parameters, results, trace bodies and free-text reasons are excluded. Follow the Lockbox/workflow IDs through existing authorized tools for that context. The relational database retains business metadata and a temporary delivery queue; large trace bodies remain outside it.

## Operator investigation example

Authorized database operators can inspect one cluster directly:

```sql
SELECT RecordedAt, OccurredAt, ControllerSequence, Kind, MachineId,
       RequesterId, ApproverId, AutomaticApproval, RequestedDefinition, RequestedVersion, Definition, Version,
       PreviousState, State, WorkflowId, RootWorkflowId, ParentWorkflowId, LockboxId
FROM DmsWorkflowAudit FINAL
WHERE ClusterId = '<cluster-id>'
ORDER BY RecordedAt, ControllerSequence;
```

Direct database access requires its own authorization. This example does not expose an arbitrary-SQL DMS command. A future public investigation endpoint must enforce the same per-cluster visibility as other DMS management queries.

## Authorized investigation queries

`GET /api/v1/dms/audit-events` and `dms operation query-audit` read archived facts directly. They do not create a Lockbox or workflow. A personal API Key and an explicit visible Cluster are required; Agent keys cannot use this management endpoint. Queries use current per-cluster visibility, including revocations, and authorized query attempts are recorded in the existing access audit.

```sh
dms operation query-audit --cluster-id "$CLUSTER" --state Failed

dms operation query-audit --cluster-id "$CLUSTER" \
  --from 2026-11-14T00:00:00Z --until 2026-11-15T00:00:00Z \
  --requested-definition cluster.disk-usage --output-format json

dms operation query-audit --cluster-id "$CLUSTER" \
  --machine-id "$MACHINE" --output-format csv
```

Filters are exact matches: `--requester-id`, `--approver-id`, `--machine-id`, `--workflow-id`, `--root-workflow-id`, `--lockbox-id`, `--definition` (the individual instance), `--requested-definition` (the original operation), `--kind`, `--state` and `--automatic-approval`. An event's State is its state at capture time, not the instance's current state. Approval events have no execution State. Use Kind to find approval/denial facts, then follow their WorkflowId or LockboxId through existing authorized diagnostics.

Time filtering uses Controller `RecordedAt`, with an inclusive `--from` and exclusive `--until`. Use ISO 8601 values with an explicit UTC offset. Defaults select the last 24 hours; a query can span at most 31 days. This limits each query, not archive retention. Select an older window to investigate older events. Responses include the normalized Query, Entries and HasNextPage. Each Entry has EventId, ControllerSequence and the structured Fact. The default table summarizes the event and retains the full WorkflowId; JSON retains all structured fields, and CSV flattens each Fact into one record, including attribution and machine IDs. Empty CSV pages still have headers.

`--page` is zero based, with 50 events per page. Ordering is RecordedAt, ControllerSequence and EventId descending after `FINAL` deduplication. Preserve the returned From and Until when requesting another page. These bounds stabilize the requested window; delivery of previously captured events can still change page boundaries. This is an eventually consistent archive, not a database snapshot. Recent changes may still be awaiting delivery. ClickHouse being disabled or unavailable returns an explicit 503, never a fabricated empty success; queries do not merge the pending queue into the archive.

The Web **Investigation** page uses the same authorization, filters and pagination, with links to workflow details, machine history and approval context. Only visible Clusters appear. Time labels and values use UTC. No arbitrary SQL, cross-cluster search, output-body search or live workflow control is exposed by this query feature.
