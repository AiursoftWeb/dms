# On-demand server operations

[Back to CLI setup](cli-and-agent.md) · [Role permissions](permissions.md) · [Author a workflow](authoring-workflows.md)

Client 1.3.0 and Agent 1.4.0 add 86 public Machine workflows in ten categories. Update the Controller, CLI and selected Agents before using these definitions. Existing commands keep their workflow names and risk. Hardware-specific tools must already be installed on the target Ubuntu/AnduinOS host; a diagnostic never installs software implicitly.

## Submit and investigate

Every request in the catalog below requires `--reason`. Select one or more explicit `--machine-id` values or `--all-machines`; selection is frozen when submitted. The target may be omitted when the Controller has exactly one active machine. Submissions return only `LockboxId` and nullable `WorkflowId`. The prefixed `lb/<UUID>` Lockbox ID is always present. Pending or denied requests create no Workflow; approval creates the actual execution root.

```sh
dms machine performance processes --machine-id "$MACHINE_ID" --sort memory --reason "Find the largest processes"
dms operation get-workflow --id "$WORKFLOW_ID"
dms operation get-callstack --id "$WORKFLOW_ID"
dms workflow trace "$WORKFLOW_ID"
dms operation get-trace --id "$CHILD_WORKFLOW_ID" --recursive false
```

Use `dms lockbox get --id "lb/<UUID>"` to inspect the decision and obtain its optional Workflow ID, then inspect the execution root and business children with these commands. Each child has its own ID. A pending request performs no machine operation. Read `get-trace` for observations and command output, and `get-workflow` for the typed result. Investigation commands support `--output-format Table`, `Json`, and `Csv`. Output formatting on a submission changes the receipt only.

After approval and execution of a `machine filesystem ls` request, run `dms workflow result "$WORKFLOW_ID"` with the root ID to see each target's directory listing together. This requires the existing Read DMS operations permission. Empty directories, incomplete listings and failed machines have distinct statuses. JSON includes the machine statuses and entries; CSV writes one row per directory entry and reports statuses needing attention on standard error. Large historical listings depend on the retained trace data. The Controller reads the pending spool as well as the archive, so results can be available before trace archival finishes.

With Client 1.23.0 and an updated Controller, follow the command printed in the table receipt. A receipt without a Workflow ID points to `dms lockbox status lb/<UUID>`. An approved listing points to `dms workflow result <WorkflowId>`. JSON and CSV retain their existing fields.

Trace entries include their UTC time, Workflow ID, definition, source and text. Workflow authors use `TraceAsync` for execution context and `WarningAsync` for incomplete observations; native process output is written as `stdout` and `stderr`. Fixed operations report their tool, time limit, elapsed time, exit code and output character counts. Completed `OperationWorkflow` functions also write their result summary. These diagnostics do not dump arguments, standard input or environment variables; operations that suppress raw output retain that choice.

Machine events are persisted in the Agent journal before delivery through its outbound WebSocket. The Controller durably spools trace events before acknowledging them and flushes them to ClickHouse `DmsWorkflowTrace`. Unacknowledged events can be retransmitted; event IDs deduplicate archive reads. Workflow metadata and Lockbox decisions remain in the relational database, with structured investigation facts archived separately to `DmsWorkflowAudit`. ClickHouse downtime retains a bounded backlog rather than silently discarding trace. Trace queries can briefly lag execution; `PendingArchiveEvents` reports the Controller-wide backlog, not just the selected workflow.

With Client 1.12.0 and an updated Controller, `workflow trace` and `operation get-trace` combine the selected workflow and all its descendants by time. Pass a child ID to read that subtree, or use `--recursive false` to read a single workflow. Each page contains up to 200 archived events. Continue with `--skip 200`, `--skip 400` and so on, keeping the selected ID and recursive option the same. Direct API calls can request the same behavior with `?recursive=true`. The web operation page displays the selected node's first 200 archived entries with links to its children.

Both trace commands default to plain log output. Use `--output-format Table` to display trace metadata and entries as a table.

```sh
dms workflow trace "$WORKFLOW_ID" | code -
dms workflow trace "$WORKFLOW_ID" > a.log
```

Each archived event occupies one line in the form `2026-11-23T06:38:42.0000000Z [INFO] Starting operation`.
The severity comes from the trace source. Engine messages and stdout use `INFO`, warnings and stderr use
`WARN`, and errors use `ERROR`. Embedded line breaks appear as `\n`. Empty pages produce no output.
Use the same `--skip` and `--recursive` options when exporting additional pages.

For disk investigations, `filesystem du` records the actual path, depth, one-filesystem scope and 60-second limit. Exit code 124 means that limit was reached. An unreadable mount or a file disappearing during a live scan can also fail `du`; earlier byte counts remain partial evidence. `layout df` and `layout inodes` explain that one unreadable mount can fail the entire query even when other filesystem rows are present. Read the native stderr for the affected path. Failed queries stay failed; their trace must not be interpreted as a complete inventory. Several Btrfs subvolume rows can represent the same filesystem and must not be added as independent capacity.

Use `--request-id` when retrying exactly the same request. Script/SQL uploads also reuse their frozen artifact identity for the same request ID and file bytes. Changed input is rejected for that ID. A started mutation with an unknown outcome is `NeedsVerification`; it is never automatically replayed. Cancellation does not roll back completed effects.

Risk permissions remain the existing five independent request/automatic/approval checkboxes. Requesting a High operation needs `CanRequestHighRiskWorkflow`; manual approval needs `CanApproveHighRiskWorkflow`. VeryHigh scripts, arbitrary SQL and unrestricted destination writes need the corresponding VeryHigh permissions. Leadership can review them when assigned the intended approval permissions; root can approve its own request only with the additional `CanApproveOwnWorkflow`. This release grants no permissions automatically.

## Command catalog

Paths in this table follow `dms machine`. All listed options are operation-specific; common submission, authentication and formatting options are described above. `required` marks mandatory options; `=value` shows a default. Run the command with `--help` for the complete syntax. Repeated package/node/column options accept multiple arguments.

### Filesystem (10)

Literal paths, bounded text reads and explicit directory recursion. File transfers reject links and special entries. `mkdir --mode` is a decimal permission bitmask: the default 448 means octal 0700.

| Command | Risk | Options |
| --- | --- | --- |
| `filesystem ls` | Low | `--path` required; `--limit=200`; `--depth=1` |
| `filesystem stat` | Low | `--path` required |
| `filesystem cat` | High | `--path` required; `--lines=100`; `--max-bytes=65536` |
| `filesystem tail` | High | `--path` required; `--lines=100`; `--max-bytes=65536` |
| `filesystem find` | Low | `--path` required; `--name`; `--min-bytes`; `--modified-after`; `--limit=200`; `--depth=5` |
| `filesystem mkdir` | High | `--path` required; `--parents`; `--mode=448` |
| `filesystem cp` | VeryHigh | `--source` required; `--destination` required; `--recursive`; `--overwrite` |
| `filesystem mv` | VeryHigh | `--source` required; `--destination` required; `--recursive`; `--overwrite` |
| `filesystem export` | High | `--path` required; `--recursive`; `--max-bytes=268435456` |
| `filesystem import` | VeryHigh | `--artifact-id` required; `--path` required; `--overwrite` |

### Layout and storage (8)

Read partition, mount, inode and open-deleted-file evidence; query SMART and bcache. SMART short tests wait for a new completed result. A started or still-running test is a warning, rather than a healthy-disk conclusion.

| Command | Risk | Options |
| --- | --- | --- |
| `layout fdisk-query` | Safe | — |
| `layout mounts` | Low | — |
| `layout inodes` | Safe | — |
| `layout open-deleted` | Low | — |
| `layout smart` | Low | `--device` required |
| `layout smart-test` | Medium | `--device` required; `--wait-seconds=120` |
| `layout bcache-info` | Low | — |
| `layout bcache-audit` | Low | `--baseline-path` |

### Processes and performance (7)

Process ownership/cgroup observations, CPU and I/O deltas, memory components, finite sampling, PSI and bounded OOM evidence. Sampling lasts at most 300 seconds. Unreadable per-process I/O and PSS are unknown, not zero.

| Command | Risk | Options |
| --- | --- | --- |
| `performance processes` | Low | `--limit=50`; `--sort=cpu` |
| `performance cpu-sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `performance memory-breakdown` | Low | — |
| `performance io-sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `performance dstat-sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `performance pressure` | Safe | — |
| `performance oom-history` | Medium | `--since`; `--until`; `--limit=200` |

### Incident history and blocked I/O (9)

Client 1.5.0, Agent 1.6.0 and Contracts/SDK 10.5.0 add the following standard workflows. Update the Controller and target Agents before submitting them. Every operation uses the existing Role risk permissions and Lockbox; no collector, tool, package or additional permission is installed automatically.

| Command | Risk | Options |
| --- | --- | --- |
| `performance history-sources` | Low | `--since`; `--until`; `--limit=200` |
| `performance history cpu` | Low | `--since`; `--until`; `--interval-seconds=60`; `--limit=200`; `--after`; `--cpu` |
| `performance history load` | Low | same time/interval/limit/after options |
| `performance history memory` | Low | same time/interval/limit/after options |
| `performance history disk` | Low | same time/interval/limit/after options; `--device` |
| `performance history network` | Low | same time/interval/limit/after options; `--interface` |
| `performance pcp-history` | Medium | `--archive-id` required; repeated `--metric` required; `--instance`; time/interval/limit/after options |
| `performance blocked-processes` | Medium | `--pid`; `--container`; `--limit=50` |
| `system kernel-events` | Medium | `--since`; `--until`; `--category`; `--boot`; `--before-cursor`; `--limit=200` |

History defaults to the hour ending at execution time. Use explicit UTC offsets and a fixed `--until` for reproducible incident queries; windows cannot exceed seven days. Queries have a 60-second reader budget, archive/output limits and at most 1,000 returned rows. `history-sources` reports existing sysstat archives and PCP archive IDs, tool versions and archive metadata. PCP metric discovery lists at most 100 names per archive. A missing optional tool or an unreadable archive produces partial evidence with a warning. Installing tools or enabling retention requires a separate approved operation.

```sh
dms machine performance history-sources --machine-id "$MACHINE_ID" --reason "Find retained incident history"
dms machine performance history disk --machine-id "$MACHINE_ID" --since 2026-11-17T14:00:00Z --until 2026-11-17T17:00:00Z --device sdc --reason "Inspect the disk queue during the incident"
dms machine performance history cpu --machine-id "$MACHINE_ID" --since 2026-11-17T14:00:00Z --until 2026-11-17T17:00:00Z --reason "Distinguish CPU execution from I/O wait"
dms machine performance pcp-history --machine-id "$MACHINE_ID" --archive-id "$ARCHIVE_ID" --metric kernel.all.load --instance '1 minute' --since 2026-11-17T14:00:00Z --until 2026-11-17T17:00:00Z --reason "Replay retained numeric load samples"
dms machine performance blocked-processes --machine-id "$MACHINE_ID" --limit 50 --reason "Identify threads currently waiting in D state"
dms machine system kernel-events --machine-id "$MACHINE_ID" --since 2026-11-17T14:00:00Z --until 2026-11-17T17:00:00Z --category io-error hung-task --reason "Search retained disk and blocked-task events"
```

Sysstat results retain native field names, explicit units and original sample intervals. `--interval-seconds` selects the first original sample per resource in each interval bucket anchored at the requested start; it does not average, interpolate or turn ten-minute observations into one-second measurements. Archive dates come from their headers, not `saDD` filenames. Restarts and missing coverage remain visible. CPU iowait is separate from CPU execution. Disk latency is milliseconds; throughput is KiB/s; memory is KiB. Interfaces and stacked block devices must not be summed as independent traffic/storage. Results are ordered by timestamp and resource. When truncated, repeat the identical explicit window and filters with `--after` set to `NextCursor`; a timestamp alone cannot safely page multiple resources.

PCP replay accepts discovered local archive IDs and literal numeric metric names/instances. It returns raw stored values, including raw counter semantics, and never evaluates derived expressions or connects to a remote PMCD. Missing values remain null. Native interval metadata may be unavailable for PCP values; archive labels and returned timestamps are evidence of coverage, not a guarantee of continuous recording. A metric that was never collected cannot be reconstructed.

`blocked-processes` reports current D-state threads, PID/TID, comm, UID, wait symbol and cgroup/container association. Hidden symbols and threads that exit during observation are reported as unknown/partial. It does not read command lines or environment, enable tracing or stop any process. A wait symbol alone does not prove a failed physical disk.

`io-sample` additionally reports actual elapsed time, read/write bytes/s and IOPS, read/write/combined await, average queue depth, busy percent and current inflight I/O. Original byte-delta fields remain available. New/reset devices and new/unreadable/reset process counters have unknown rates; a device that disappears is listed separately. Await is null when no operations completed. Device busy is not a multi-queue saturation verdict.

`system journal` also accepts `--kernel` and `--before-cursor`. Kernel events select fixed categories (`oom`, `hung-task`, `io-error`, `filesystem`, `reset`) inside journalctl before its result limit. Results are newest first, preserve multiline records, and expose `HasMore`, `NextCursor`, requested/observed bounds and native diagnostics. For another page, repeat the same filters/window with `--before-cursor` set to `NextCursor`; equal timestamps do not lose records. Kernel transport does not implicitly restrict the query to the current boot. A no-match result cannot prove absence of an incident when retention is incomplete.

Read operation results and child trace using the normal IDs:

```sh
dms operation get-workflow --id "$WORKFLOW_ID"
dms operation get-callstack --id "$WORKFLOW_ID"
dms operation get-trace --id "$CHILD_WORKFLOW_ID" --output-format json
```

Valid partial results are Warning with `Complete=false`; invalid input, corrupt/unsupported response formats and execution errors are not successful empty measurements. Large history reports can also be returned as a normal authorized JSON artifact when Agent artifact transport is available. These commands use retained data and finite observations; they do not add continuous process/network collection or recover missing historical client attribution.

### NVIDIA (7)

Driver-reported snapshots, compute/graphics processes, finite samples, ECC/Xid evidence, topology and CUDA compatibility. N/A stays unavailable; shared-memory GPUs need not expose all discrete-GPU fields.

| Command | Risk | Options |
| --- | --- | --- |
| `nvidia smi` | Safe | — |
| `nvidia overview` | Low | — |
| `nvidia processes` | Low | — |
| `nvidia sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `nvidia errors` | Low | — |
| `nvidia topology` | Low | — |
| `nvidia driver-info` | Low | — |

Run `dms machine nvidia overview --machine-id "$MACHINE_ID" --reason "Inspect GPU usage"`, then use
`dms workflow trace "$WORKFLOW_ID"` to see the driver, each GPU's utilization and framebuffer memory,
driver-reported compute and graphics processes, and the topology matrix. The GB10 reports host-wide unified memory
separately. Unavailable GPU readings are shown as `N/A`. An unavailable process list or topology is reported as a
warning and collected information remains in the trace. Update the Controller, CLI and target Agent for this command.

### Docker containers and files (10)

Runtime/health/image evidence excludes raw container environment and secret contents. Lifecycle commands accept complete container IDs and refuse Swarm tasks. File copy commands stay on the selected machine and its local Docker socket.

| Command | Risk | Options |
| --- | --- | --- |
| `docker inspect` | Low | `--container` required |
| `docker stats-sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `docker events` | Low | `--container` required; `--since`; `--until`; `--limit=200` |
| `docker diagnose-startup` | Medium | `--container` required |
| `docker stop` | High | `--container` required; `--timeout-seconds=30` |
| `docker start` | High | `--container` required |
| `docker restart` | High | `--container` required; `--timeout-seconds=30` |
| `docker filesystem export` | High | `--container` required; `--path` required; `--recursive`; `--max-bytes=268435456` |
| `docker filesystem cp-to` | VeryHigh | `--container` required; `--source` required; `--destination` required; `--recursive`; `--overwrite` |
| `docker filesystem cp-from` | VeryHigh | `--container` required; `--source` required; `--destination` required; `--recursive`; `--overwrite` |

### Swarm services (9)

Manager-local operations. Changes require an expected service version and the explicit Swarm node IDs displayed by service inspect. Replicated services are supported; global/job services are rejected before changing them. See rollout semantics below.

| Command | Risk | Options |
| --- | --- | --- |
| `swarm service list` | Low | — |
| `swarm service inspect` | Low | `--service` required |
| `swarm service ps` | Low | `--service` required |
| `swarm service logs` | Medium | `--service` required; `--lines=100`; `--since` |
| `swarm service rollout-status` | Low | `--service` required. The previous `update-status` spelling is an alias. |
| `swarm service stop` | High | `--service` required; `--expected-version` required; `--node-id` required; `--timeout-seconds=120` |
| `swarm service start` | High | `--service` required; `--expected-version` required; `--node-id` required; `--replicas=1`; `--timeout-seconds=120` |
| `swarm service update` | High | `--service` required; `--expected-version` required; `--node-id` required; `--image` required; `--timeout-seconds=120` |
| `swarm service rollback` | High | `--service` required; `--expected-version` required; `--node-id` required; `--timeout-seconds=120` |

### System, systemd and APT (15)

System/clock/boot observations, systemd state/journal/timers, package inspection and approved changes. Agent lifecycle uses its dedicated workflow. Reboot confirmation and arbitrary script execution have the additional rules below.

| Command | Risk | Options |
| --- | --- | --- |
| `system info` | Safe | — |
| `system uptime` | Safe | — |
| `systemctl list` | Low | `--failed-only`; `--limit=200` |
| `systemctl start` | Medium | `--service` required |
| `systemctl stop` | High | `--service` required |
| `system journal` | Medium | `--service`; `--boot`; `--level`; `--since`; `--until`; `--limit=200`; `--kernel`; `--before-cursor` |
| `system timers` | Medium | — |
| `apt inspect` | Low | — |
| `apt install` | High | `--package` required |
| `apt upgrade` | High | `--package name=version` required |
| `apt update` | Medium | — |
| `apt upgrade-all` | Low (preview), High (apply) | `--dry-run` or `--plan <preview-workflow-id>` |
| `apt autoremove` | Low (preview), High (apply) | `--dry-run` or `--plan <preview-workflow-id>`; `--purge` |
| `system reboot` | High | `--delay-seconds=15` |
| `system execute-script` | VeryHigh | `--language` required; `--script-file` required; `--timeout-seconds=60` |

### Databases (8)

One public command family supports registered MySQL, PostgreSQL and ClickHouse profiles. Passwords stay in private machine files. Database discovery reports observed targets; metadata/query commands authenticate a particular connection.

| Command | Risk | Options |
| --- | --- | --- |
| `database discover` | Low | — |
| `database list-databases` | Low | `--connection` required |
| `database list-tables` | Low | `--connection` required; `--database` required |
| `database describe-table` | Low | `--connection` required; `--database` required; `--table` required |
| `database query-table` | Medium | `--connection` required; `--database` required; `--table` required; `--column` required; `--filters`; `--sort-by`; `--descending`; `--limit=100`; `--offset=0` |
| `database query-sql` | High | `--connection` required; `--sql-file` required; `--timeout-seconds=30`; `--max-rows=1000` |
| `database execute-sql` | VeryHigh | `--connection` required; `--sql-file` required; `--timeout-seconds=30`; `--max-rows=1000` |
| `database diagnostics` | Medium | `--connection` required |

### Networking (8)

Machine-local routes, interface/socket ownership, resolver answers, bounded endpoint probes and traffic metadata, firewall rules/counters and TLS validation. Traffic samples do not capture application payloads.

| Command | Risk | Options |
| --- | --- | --- |
| `networking interfaces` | Low | — |
| `networking routes` | Low | — |
| `networking sockets` | Low | — |
| `networking dns-query` | Low | `--host` required |
| `networking probe` | Low | `--host` required; `--port=443`; `--url`; `--timeout-seconds=10` |
| `networking traffic-sample` | Medium | `--seconds=10`; `--interval=1`; `--limit=50` |
| `networking firewall-info` | Low | — |
| `networking tls-check` | Low | `--host` required; `--port=443`; `--url`; `--timeout-seconds=10` |

### Security observations and baselines (8)

Observed Secure Boot and encryption state, effective SSH settings for an explicit Match context, Fail2ban/CleanTraffic status, authentication events, account metadata and versioned baseline comparison. Missing evidence remains unknown.

| Command | Risk | Options |
| --- | --- | --- |
| `security secure-boot` | Safe | — |
| `security disk-encryption` | Low | — |
| `security ssh-audit` | Low | `--user=root`; `--address=127.0.0.1`; `--host=localhost` |
| `security fail2ban-status` | Low | — |
| `security clean-traffic-status` | Low | — |
| `security auth-events` | Medium | `--since`; `--until`; `--limit=200` |
| `security accounts-audit` | Medium | — |
| `security baseline-audit` | Medium | `--baseline-path` |

## Files and directories

An export workflow reads the approved path after approval and returns an immutable artifact ID, SHA256, length and kind in its typed result. The bytes travel over the Agent's outbound HTTP connection to its Controller. They are stored privately under `Dms:StatePath/artifacts`, outside ClickHouse and the relational database. Trace contains metadata and progress, not the binary payload.

Run `dms artifact ls` to see the IDs, names, sizes and expiration times of your completed, unexpired artifacts. It needs Run open workflows and is automatically approved and audited. Use `--limit` from 1 to 20 (default 20), `--offset` to skip entries in newest-first order, or `--output-format json` or `csv`. A blank expiration indicates an input retained for audit. Workflow status and the call tree show execution metadata. Only the requester can retrieve the listing through its Open Lockbox result.

To download a file, copy its ID from `dms artifact ls` and run `dms artifact download --id "$ARTIFACT_ID" --file ./report.tar`.

```sh
dms machine filesystem export --machine-id "$MACHINE_ID" --path /srv/reports --recursive --reason "Export these reports"
# After approval, inspect callstack and the Machine child's result for Artifact.Id.
dms artifact get --id "$ARTIFACT_ID"
dms artifact download --id "$ARTIFACT_ID" --file ./reports.tar
```

Container export uses `docker filesystem export --container <full-id> --path /path --recursive`. Downloading an artifact is an authenticated, audited delivery action. It does not run another machine workflow. The ID alone grants no download permission: access belongs to its owner, or to an authorized reviewer of a request using that exact input.

```sh
dms artifact upload --file ./settings.json
dms machine filesystem import --machine-id "$MACHINE_ID" --artifact-id "$ARTIFACT_ID" --path /srv/settings.json --reason "Import the reviewed file"
```

Import resolves the artifact's actual metadata before submission and freezes that manifest in the approval. Directory uploads use an uncompressed relative-path tar with `dms artifact upload --file ./reports.tar --kind directory-tar`. Imported files receive private permissions. The archive must contain regular files/directories only: absolute or parent paths, links, devices, sockets, duplicate files and more than 10,000 entries are rejected. Directory overwrite must be explicit and merges entries; it preserves unrelated destination entries. Path operations reject symlink components. Directory transfers refuse nested mount points and are captures over an interval, rather than atomic filesystem snapshots.

Each artifact is limited to one GiB; exports default to 256 MiB. Transfers use eight-MiB upload chunks and validated offsets. A repeated chunk must have identical bytes. Interrupted downloads resume the same immutable artifact; the complete length and SHA256 must match before the local destination is replaced. Local overwrite requires `--overwrite`. Failed/cancelled downloads remove their private temporary file.

Temporary transfers expire after 24 hours and are reclaimed when a new upload begins. Script and SQL source files referenced by submitted requests are retained for approval audit, including after a decision. Capacity defaults to ten GiB and can be configured with `Dms:Artifacts:MaximumTotalBytes` (one GiB to one TiB). Capacity exhaustion refuses new uploads. Keep this directory in the Controller's normal persistent-state and backup procedure.

## Database profiles and SQL

An administrator registers connections in `/etc/aiursoft-dms-agent/database-connections.json`, readable only by the Agent account. Passwords live in separate 0600 files on the Agent host. For example, replace these demonstration values with the intended target and private password-file path:

```json
[
  {
    "Name": "reporting-read",
    "Engine": "postgresql",
    "Host": "database.internal",
    "Port": 5432,
    "User": "dms_reader",
    "Database": "reporting",
    "PasswordFile": "/etc/aiursoft-dms-agent/reporting-read.password",
    "ReadOnly": true,
    "Tls": true,
    "Container": null
  }
]
```

Engines are `mysql`, `postgresql` or `clickhouse`. TLS is enabled by default and verifies the host/certificate through the native client or HTTPS. Explicitly disabling TLS should be limited to an appropriate trusted deployment. MySQL/PostgreSQL profiles may specify a complete local container ID to use its installed native client; host, port and database then refer to that container's network view. ClickHouse uses a reachable HTTP(S) endpoint and does not accept `Container`. A PostgreSQL connection names one database; register separate profiles for other databases.

Profiles name business database targets. Machine workflows do not receive the Controller's inventory database context. `diagnostics` queries engine activity, locks, storage and available slow-query statistics. MySQL uses performance_schema digest statistics, PostgreSQL uses the optional pg_stat_statements extension, and ClickHouse uses system.query_log. Missing privileges/extensions are explicit warnings; DMS does not install them in a diagnostic. Slow-query metadata excludes raw statement text.

Read workflows require a dedicated `ReadOnly: true` profile. MySQL readers may have only direct SELECT/SHOW VIEW/USAGE/PROCESS grants; role-based and privileged grants are refused. PostgreSQL readers cannot be superusers, create roles/databases, bypass RLS or use server-file/program roles. Read-only sessions, statement deadlines, output/row limits and an SQL grammar/function allowlist are additional restrictions. Configure the underlying database account with the required least privileges; a profile flag alone does not create database grants. Diagnostic system tables may require separate read grants, and unavailable sources are warnings.

```sh
dms machine database list-tables --connection reporting-read --database reporting --machine-id "$MACHINE_ID" --reason "Inspect available tables"
dms machine database query-table --connection reporting-read --database reporting --table events --column id name --filters '[{"Column":"name","Operator":"eq","Value":"example"}]' --limit 100 --machine-id "$MACHINE_ID" --reason "Inspect matching events"
dms machine database query-sql --connection reporting-read --sql-file ./investigate.sql --machine-id "$MACHINE_ID" --reason "Review this read query"
dms machine database execute-sql --connection reporting-write --sql-file ./change.sql --machine-id "$MACHINE_ID" --reason "Apply the reviewed SQL"
```

Typed filters accept `eq`, `ne`, `lt`, `le`, `gt`, `ge`, `contains` and `is-null`, joined with AND. Identifiers are validated and values are encoded as data. Arbitrary WHERE text belongs in the High-risk `query-sql` command. SQL files are bounded UTF-8 input (128 KiB); they are uploaded before submission and remain visible from the Web Lockbox's frozen-input link. Query text is not repeated into general trace. Results still may contain sensitive business values, so grant investigation access accordingly.

`execute-sql` is VeryHigh and may use a separately registered write profile. It can perform DML/DDL and multiple statements. Engine transaction rules apply: no generic rollback is promised. ClickHouse mutations wait for completion on active replicas within the approved timeout; an unavailable response or incomplete mutation can still mean changes occurred. Failed/interrupted writes become `NeedsVerification` and are not retried. The command does not interpret MySQL/psql shell escape commands. Credentials and SQL text are not placed in process arguments.

## Service updates, packages and reboot

Use the dedicated Box deployment workflows for Box-managed services when their repository is the authoritative configuration. Swarm service update supports an explicit image change, without arbitrary entrypoint, mounts, environment, privileges or Docker arguments. Inspect the service first to obtain its current version and node IDs:

```sh
dms machine swarm service inspect --service example_service --machine-id "$MANAGER_ID" --reason "Inspect rollout scope"
dms machine swarm service update --service example_service --expected-version 123 --node-id "$SWARM_NODE_ID" --image example/image:version --machine-id "$MANAGER_ID" --reason "Roll out this version on the approved node"
```

Service changes refuse drift in the expected version or active task scope. They add a per-service `aiursoft.dms.service-scope.<service-id>=true` label to approved nodes and a corresponding placement constraint, preserving other labels and constraints. Node labels may have changed if a later step fails. Expanding/changing a previous node scope requires all affected nodes in the new approval. `stop` sets a replicated service to zero and records its previous desired replicas; `start` uses the approved explicit replica count. `rollback` uses the previous specification from the approved version. Global/job modes are rejected.

Convergence checks desired replicas, actual task states, exact image strings/digests and update/rollback state. Running tasks on remote nodes do not independently prove application readiness; inspect their container health or run an explicit approved probe. A timeout after mutation is `NeedsVerification`, with no automatic retry or rollback. Stopping a service uses its configured stop behavior; application connection draining remains an application concern.

For fleet maintenance, refresh the indexes first and wait for the target workflows to complete. Run a preview against the machines you want to maintain. Preview uses each machine's current indexes. Read the per-machine results with `apt plan` and use the same preview workflow ID to submit the exact plan for approval.

```sh
dms machine apt update --all-machines --reason "Refresh package indexes"
dms machine apt upgrade-all --all-machines --dry-run --reason "Plan package upgrades"
dms machine apt plan "$UPGRADE_PREVIEW_ID"
dms machine apt upgrade-all --plan "$UPGRADE_PREVIEW_ID" --reason "Upgrade reviewed packages"

dms machine apt autoremove --all-machines --purge --dry-run --reason "Preview unused packages and configuration"
dms machine apt plan "$CLEANUP_PREVIEW_ID"
dms machine apt autoremove --purge --plan "$CLEANUP_PREVIEW_ID" --reason "Purge reviewed unused packages"
```

Use the root workflow ID from the approved preview. Omit the target flags when applying to reuse the preview's targets, or use `--machine-id` to select a successfully previewed subset. Pass `--purge` to both commands when you want to remove package configuration. Upgrading allows new dependencies and excludes any transaction that removes packages. Upgrades found only in a plan that would remove packages are listed separately for review. APT holds and phased updates continue to apply. The Lockbox shows the plans for each target. If a target's proposed package actions change, its workflow stops and reports the difference. Other targets use their own plans. The preview reports a workflow size error if a target’s plan exceeds the transport limits.

APT install/upgrade uses the already configured trusted APT sources and simulates the plan before applying it. It refuses removals or additional unapproved package names. Include required dependencies explicitly. Upgrade requires every package as `name=version`; an altered plan is rejected. No package-index refresh or unapproved service deployment is implicit. Package operations can run maintainer scripts and restart services; review their High risk accordingly.

```sh
dms machine apt install --package smartmontools --machine-id "$MACHINE_ID" --reason "Install the approved diagnostics tool"
dms machine apt upgrade --package example-package=1.2.3 --machine-id "$MACHINE_ID" --reason "Apply this frozen package version"
dms machine system reboot --delay-seconds 5 --machine-id "$MACHINE_ID" --reason "Reboot during the approved window"
```

Reboot requires an installed Agent identity and durable journal. It records the old kernel boot ID before handing the reboot to systemd. The same installation must reconnect with a new boot ID within 15 minutes to confirm success. A successful scheduling command alone is insufficient. No new boot ID, expired confirmation, changed identity or uncertain outcome remains `NeedsVerification`; a reboot is never replayed. A reboot subworkflow gets its own ID; a lost parent C# stack cannot be resumed after the machine restarts.

`system execute-script --language bash|python --script-file ./task --timeout-seconds 60` is the explicit VeryHigh escape hatch. The actual frozen source is available to the authorized approver on the Web. Its output uses normal workflow trace. It does not grant lower-risk operations an arbitrary shell option.

## Health and compliance conclusions

Baseline audit requires an explicit JSON baseline path on the target. A bcache baseline contains `Version`, `ExpectedCacheMode`, `RequireAttachedCache` and `RequireRunning`. A security baseline contains `Version`, `RequireSecureBoot`, `EncryptedMounts`, `RequireCertificateCa`, `RequireFail2ban`, `RequireCleanTraffic` and an optional `BcacheBaselinePath`. Its defaults require Secure Boot, encryption of `/`, an SSH certificate CA, Fail2ban and CleanTraffic; SSH password/root/public-key checks are always evaluated. With no baseline or missing sources, results are observations and Unknown/Warning rather than a compliance pass.

Secure Boot reports firmware evidence, not remote attestation. Encryption reports each visible block-backed mount's ancestry. SSH password/keyboard-interactive, root login, ordinary public keys and certificate CA configuration are separate observations for the supplied user/address/host Match context. A configured CA path is reported as configuration, not proof that a particular certificate can log in. A large traffic sample is evidence to investigate and does not itself identify an attacker.
