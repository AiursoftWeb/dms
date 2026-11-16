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

## Access and authentication

Configure local accounts or OIDC through the existing Template settings. When deploying with OIDC, set `AppSettings__AuthProvider=OIDC` explicitly and provide the identity provider settings; Local remains the source default.

Workflow authorization uses the existing role Permission claims globally within one DMS instance. Five risk levels each have independent request, automatic approval and approval permissions; self approval is a separate Permission. No risk implies another. Read DMS operations permits inspection across all machines. Cluster fields temporarily remain inventory filters, with no authorization effect. See [role permissions](permissions.md) for configuration and upgrade requirements.

Personal API keys identify CLI users. Machine API keys identify Agents. Keep both types private and revoke them through their management pages when no longer needed.

## Recovery and cancellation

Unstarted tasks resume dispatch after a Controller restart. A started task whose result cannot be confirmed becomes `NeedsVerification` and is never automatically rerun. Investigate its actual machine state before deciding what to do next.

`operation kill-workflow` requires a new Lockbox. Once approved, it requests cooperative cancellation of the target workflow. Cancellation does not undo completed side effects or prove that an action already handed to systemd was rolled back.

Use workflow IDs, parent/child callstacks, results and trace together when investigating an interrupted operation.
