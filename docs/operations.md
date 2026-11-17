# On-demand server operations

[Back to CLI setup](cli-and-agent.md) · [Role permissions](permissions.md) · [Author a workflow](authoring-workflows.md)

Client 1.3.0 and Agent 1.4.0 add 86 public Machine workflows in ten categories. Update the Controller, CLI and selected Agents before using these definitions. Existing commands keep their workflow names and risk. Hardware-specific tools must already be installed on the target Ubuntu/AnduinOS host; a diagnostic never installs software implicitly.

## Submit and investigate

Every command in the catalog below requires `--reason`. Select one or more explicit `--machine-id` values or `--all-machines`; selection is frozen when submitted. The target may be omitted when the Controller has exactly one active machine. Submissions return only `WorkflowId`, including when automatic approval is enabled.

```sh
dms machine performance processes --machine-id "$MACHINE_ID" --sort memory --reason "Find the largest processes"
dms operation get-workflow --id "$WORKFLOW_ID"
dms operation get-callstack --id "$WORKFLOW_ID"
dms operation get-trace --id "$CHILD_WORKFLOW_ID"
```

Inspect the root Lockbox and business children with these existing commands. Each child has its own ID. A pending request performs no machine operation. Read `get-trace` for observations and command output, and `get-workflow` for the typed result. Investigation commands default to tables and support `--output-format json` or `csv`. Output formatting on a submission changes the receipt only.

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

### NVIDIA (6)

Driver-reported snapshots, compute/graphics processes, finite samples, ECC/Xid evidence, topology and CUDA compatibility. N/A stays unavailable; shared-memory GPUs need not expose all discrete-GPU fields.

| Command | Risk | Options |
| --- | --- | --- |
| `nvidia smi` | Safe | — |
| `nvidia processes` | Low | — |
| `nvidia sample` | Low | `--seconds=10`; `--interval=1`; `--limit=50` |
| `nvidia errors` | Low | — |
| `nvidia topology` | Low | — |
| `nvidia driver-info` | Low | — |

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
| `swarm service update-status` | Low | `--service` required |
| `swarm service stop` | High | `--service` required; `--expected-version` required; `--node-id` required; `--timeout-seconds=120` |
| `swarm service start` | High | `--service` required; `--expected-version` required; `--node-id` required; `--replicas=1`; `--timeout-seconds=120` |
| `swarm service update` | High | `--service` required; `--expected-version` required; `--node-id` required; `--image` required; `--timeout-seconds=120` |
| `swarm service rollback` | High | `--service` required; `--expected-version` required; `--node-id` required; `--timeout-seconds=120` |

### System, systemd and APT (12)

System/clock/boot observations, systemd state/journal/timers, package inspection and approved changes. Agent lifecycle uses its dedicated workflow. Reboot confirmation and arbitrary script execution have the additional rules below.

| Command | Risk | Options |
| --- | --- | --- |
| `system info` | Safe | — |
| `system uptime` | Safe | — |
| `systemctl list` | Low | `--failed-only`; `--limit=200` |
| `systemctl start` | Medium | `--service` required |
| `systemctl stop` | High | `--service` required |
| `system journal` | Medium | `--service`; `--boot`; `--level`; `--since`; `--until`; `--limit=200` |
| `system timers` | Medium | — |
| `apt inspect` | Low | — |
| `apt install` | High | `--package` required |
| `apt upgrade` | High | `--package` required |
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
