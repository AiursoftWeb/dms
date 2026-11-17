# Configuration and persistence

[Back to README](../README.md)

DMS uses the standard Aiursoft Template database, account, OIDC, file storage and application logging settings. The defaults run with SQLite and local accounts. Shared configuration is described in the [Template documentation](https://template-docs.aiursoft.com/).

## Controller settings

ASP.NET Core environment overrides use double underscores, such as `Dms__StatePath` or `AppSettings__AuthProvider`.

| Setting | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings:DbType` | `Sqlite` | Select SQLite or MySQL. |
| `ConnectionStrings:DefaultConnection` | `DataSource=app.db;Cache=Shared` | Business database containing inventory, access, Lockboxes and workflow metadata. |
| `Storage:Path` | `/tmp/data` | Shared Template file storage. |
| `AppSettings:AuthProvider` | `Local` | Local accounts; set `OIDC` and configure `AppSettings:OIDC` to use an identity provider. |
| `Dms:StatePath` | `dms-state` | Persistent Controller workflow state and detailed trace backlog. |
| `Dms:Artifacts:MaximumTotalBytes` | `10737418240` | Total reserved artifact capacity in bytes, including temporary uploads and retained script/SQL approval sources. One GiB to one TiB. |
| `Dms:Clickhouse:Enabled` | `false` | Enable archival and querying of detailed workflow traces. |
| `Dms:Clickhouse:ConnectionString` | Local ClickHouse HTTP endpoint | Connection to the trace archive; configure the actual host, database and credentials before enabling it. |

For example, after preparing a reachable ClickHouse database:

```text
Dms__StatePath=/var/lib/dms/state
Dms__Clickhouse__Enabled=true
Dms__Clickhouse__ConnectionString=Host=clickhouse;Protocol=http;Port=8123;Username=dms;Password=<secret>;Database=dms
```

Keep real credentials outside Git. `Logging:Clickhouse` controls ordinary application logs separately from the DMS workflow trace archive.

## Persistent storage

Run one Controller with its own persistent database and `Dms:StatePath` directory. The current design does not support multiple Controllers sharing a workflow store.

The Docker image stores its first-start configuration in `/data/appsettings.json` and reuses it on later starts. Its defaults place the SQLite database, Template file storage and `Dms__StatePath=/data/dms-state` under `/data`. Keep that volume across container replacement. If you override any paths or use MySQL, persist those stores separately as well.

Detailed workflow traces are first written to the Controller's disk queue, then archived to the ClickHouse table `DmsWorkflowTrace`. They are not stored in the relational business database. Prepare ClickHouse before using DMS for ongoing operations. With archival disabled or unavailable, traces remain in the disk backlog; unavailable storage applies backpressure rather than silently dropping entries.

Preserve each Agent's private `DMS_STATE_PATH` directory independently of the Controller volume. It contains its installation identity and task journal. See [Agent setup](./cli-and-agent.md#run-an-agent).

File artifacts use the private `artifacts` subdirectory of `Dms:StatePath`. Temporary transfers expire after 24 hours; submitted script/SQL input is retained for audit. Binary bytes are not stored in the relational database or ClickHouse. See [file transfer and database setup](operations.md).

## Access and authentication

Configure local accounts or OIDC through the existing Template settings. When deploying with OIDC, set `AppSettings__AuthProvider=OIDC` explicitly and provide the identity provider settings; Local remains the source default.

Workflow authorization uses the existing role Permission claims globally within one DMS instance. Five risk levels each have independent request, automatic approval and approval permissions; self approval is a separate Permission. No risk implies another. Read DMS operations permits inspection across all machines. All machines belong directly to this DMS instance. See [role permissions](permissions.md) for configuration and upgrade requirements.

Personal API keys identify CLI users. Machine API keys identify Agents. Keep both types private and revoke them through their management pages when no longer needed.

## Upgrade to instance-wide inventory

Controller and Client 1.1.0 remove the Cluster resource and all `--cluster-id` / `--cluster-name` parameters. A Cluster Workflow still means C# orchestration on the Controller, including inventory access and typed subworkflow calls. Separate DMS instances use separate business databases and ClickHouse namespaces; they do not share an inventory or API keys.

Before upgrading an existing installation, stop its old Controller and preserve the business database, state directory and archive through the deployment's approved backup procedure. Run only one Controller against these stores. Do not start an old Controller against the migrated schema. Downgrade cannot reconstruct former group memberships and is explicitly refused; recovery requires restoring the pre-upgrade stores and the matching application version.

The relational migrations retain users, roles, permission claims, personal keys, machine/installation identities, join keys, enrollments, decisions, workflow IDs and states. They remove only the old resource/grant tables and fields, including the unused stored self-approval flag. Stored request parameters and undelivered audit facts lose their old resource field without changing event IDs, request IDs or frozen machine lists. Old scoped grants are never converted into global permission claims.

When ClickHouse archival is enabled, the first audit initialization rebuilds the old table into `DmsWorkflowAuditMigration`, copies deduplicated facts, verifies event counts and a fingerprint over every retained column, atomically exchanges the tables, and removes staging. The database must use the Atomic or Shared engine for [atomic table exchange](https://clickhouse.com/docs/sql-reference/statements/exchange). Stop the old Controller before this step. A partial copy is safely rebuilt on retry; after a successful exchange, startup only finishes cleanup. A verification failure leaves the original authoritative table and durable outbox intact, and audit queries report an unavailable archive. Allow temporary space for the copy. The detailed trace table and disk queue do not change.

Existing connected Agents retain their identities and task journals; registration does not need to be repeated. Use Agent 1.1.0 for the simplified join command. Installed Agents can receive it through the existing approved Agent upgrade workflow. On loading/saving the new configuration, the removed group field is discarded while Controller, machine ID, installation ID and credentials stay unchanged. Check the explicit role permission matrix before resuming operations; an API key's owner still needs the appropriate request/approval permissions.

## Recovery and cancellation

Unstarted tasks resume dispatch after a Controller restart. A started task whose result cannot be confirmed becomes `NeedsVerification` and is never automatically rerun. Investigate its actual machine state before deciding what to do next.

`operation kill-workflow` requires a new Lockbox. Once approved, it requests cooperative cancellation of the target workflow. Cancellation does not undo completed side effects or prove that an action already handed to systemd was rolled back.

Use workflow IDs, parent/child callstacks, results and trace together when investigating an interrupted operation.
