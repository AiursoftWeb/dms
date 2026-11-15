# CLI and Agent setup

[Back to README](../README.md)

The Controller provides the web UI and HTTP API. The CLI submits requests and inspects results; an Agent runs approved machine workflows on its own Linux host. The SDK is available in [Aiursoft.DMS.SDK](../src/Aiursoft.DMS.SDK).

## Build the clients

From the repository root, use the .NET 10 SDK to publish the CLI and Agent:

```sh
dotnet publish src/Aiursoft.DMS.Client -c Release \
  --no-self-contained -p:UseAppHost=false -o ./bin/client
dotnet publish src/Aiursoft.DMS.Agent -c Release \
  --no-self-contained -p:UseAppHost=false -o ./bin/agent
dotnet ./bin/client/dms.dll --help
```

These are framework-dependent applications. Install the .NET 10 runtime on each host that runs one, and distribute the entire publish directory. The CLI and Agent are not currently packaged as installable NuGet tools.

## Configure access

Sign in through the Controller's configured authentication provider and create a personal key under **API Keys**. Configure the CLI with your Controller URL and key:

```sh
export DMS_SERVER=https://dms.example.com
export DMS_API_KEY='<personal-key>'
dotnet ./bin/client/dms.dll access whoami
```

`--server` and `--api-key` can also be supplied to individual commands. `whoami` shows the current user's ID, platform permissions and self-approval setting. Machine keys authenticate Agents.

### Manage clusters and permissions

Users with **Manage DMS access** can create clusters and configure access through the CLI or **Cluster access** web page. Use `access whoami` or the administrator-only `access get` snapshot to find user IDs:

```sh
dotnet ./bin/client/dms.dll cluster create --name aiursoft-prod
dotnet ./bin/client/dms.dll cluster list
dotnet ./bin/client/dms.dll access get
dotnet ./bin/client/dms.dll access set-grant \
  --cluster-id <cluster-id> --user-id <user-id> --risk Medium \
  --can-request --can-approve
```

`set-grant` replaces all three flags for exactly one user, cluster and risk. Omitted flags are false; the example permits requesting and approving Medium operations, with automatic approval disabled. Repeat for each required risk level. Creating a cluster grants no operation permissions automatically. Self approval is configured separately:

```sh
dotnet ./bin/client/dms.dll access set-self-approval \
  --user-id <user-id> --enabled false
```

`cluster list` shows administrators all clusters and other users only their authorized clusters. These configuration and identity commands are audited Controller management operations and create no Lockbox. Server operations such as `cluster get-machines` and `cluster add-machine` continue through Lockbox.

`cluster create --id <creation-id> --name <name>` can safely retry a creation with the same ID and name. A reused ID with another name or a duplicate cluster name is rejected. Administrators can rename or remove a cluster:

```sh
dotnet ./bin/client/dms.dll cluster rename --cluster-id <cluster-id> --name <new-name>
dotnet ./bin/client/dms.dll cluster drop --cluster-id <cluster-id>
```

Deletion requires no machines and no operation history. Its access grants are removed atomically and the audit record is retained.

## Register a machine

Replace the example IDs with the IDs from your deployment:

```sh
dotnet ./bin/client/dms.dll cluster add-machine \
  --cluster-id <cluster-id> --name server-01 --reason 'Register server'
dotnet ./bin/client/dms.dll lockbox get --id <lockbox-id>
dotnet ./bin/client/dms.dll lockbox approve \
  --id <lockbox-id> --reason 'Registration approved'
```

The submission returns a Lockbox ID. A permitted approver must approve it unless the requester's cluster permission allows automatic approval. After approval, inspect the operation result for the machine ID. Issue that machine's Agent key using the CLI or **Cluster access**:

```sh
dotnet ./bin/client/dms.dll access issue-agent-key --machine-id <machine-id>
```

The response contains the new key once; issuing another key revokes the previous key. Access snapshots omit keys and stored key hashes. Preserve the machine's existing installation identity when rotating its key.

## Run an Agent

Copy the Agent publish directory to the managed Linux host and provide a private persistent state directory writable by the Agent's account:

```sh
export DMS_CONTROLLER=https://dms.example.com
export DMS_MACHINE_KEY='<agent-key>'
export DMS_STATE_PATH=/var/lib/dms-agent
dotnet /opt/dms-agent/Aiursoft.DMS.Agent.dll
```

The Agent opens the WebSocket connection to the Controller. It does not require inbound SSH, internet access or a database connection; the Controller must remain reachable. The Agent account needs the operating-system permissions required by the workflows you intend to run.

The machine is bound to the installation identity in its state directory. Preserve this directory across upgrades. A second installation with the same key is rejected until pending operations have been reconciled.

## Discover standard workflows

Use `dms operation list-definitions --cluster-id <cluster-id>` to see the Controller's registered public standard workflows and your permissions in that cluster. The command supports `--output-format table|json|csv` and creates no Lockbox. The same catalog is available under **Standard workflows** in the web UI. See [workflow discovery](workflow-discovery.md) for permission and output details.

## Request and inspect operations

```sh
dotnet ./bin/client/dms.dll cluster get-machines \
  --cluster-id <cluster-id> --reason 'Inspect inventory'
dotnet ./bin/client/dms.dll machine performance info \
  --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect capacity'
dotnet ./bin/client/dms.dll machine agent status \
  --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect Agent'
dotnet ./bin/client/dms.dll machine systemctl status \
  --cluster-id <cluster-id> --machine-id <machine-id> \
  --service example.service --reason 'Inspect service'
dotnet ./bin/client/dms.dll machine layout lsblk \
  --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect block devices'
dotnet ./bin/client/dms.dll machine layout df \
  --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect local capacity'
dotnet ./bin/client/dms.dll machine systemctl restart \
  --cluster-id <cluster-id> --machine-id <machine-id> \
  --service example.service --reason 'Approved maintenance'
dotnet ./bin/client/dms.dll operation get-workflow --id <workflow-id>
dotnet ./bin/client/dms.dll operation get-callstack --id <workflow-id>
dotnet ./bin/client/dms.dll operation get-trace --id <workflow-id>
```

Each operational command creates a Lockbox before execution. The Lockbox ID, its own workflow ID and the approved operation's workflow ID are distinct; use the returned ID for the corresponding inspection command. Inspection and `lockbox approve`/`deny` use authenticated management APIs without creating another Lockbox.

Use `--all-machines` instead of `--machine-id` to freeze the cluster's current machines into a request. A request targets only one cluster. Use `--request-id` when retrying the same submission and `--output-format json` for automation.

`operation kill-workflow --cluster-id <cluster-id> --id <workflow-id> --reason 'Stop operation'` creates a new Lockbox. Cancellation requests cooperative termination; it does not roll back completed side effects. See [recovery and cancellation](./configuration.md#recovery-and-cancellation).

Agent status, performance information, `layout lsblk` and `layout df` use the Safe risk policy; `systemctl status` uses Low and restart uses Medium. Service commands require a concrete `.service` name. Status reads fixed systemd properties without reading the journal. Block-device queries exclude loop devices, and capacity queries cover local filesystems. No extra command arguments or shell expressions are accepted.

Read-only process results contain `ExitCode`, `Output` and `ErrorOutput`; the output is also available in the workflow trace. Each output stream is limited to 8192 characters, and the serialized result to 24000 characters. Larger queries fail explicitly rather than returning truncated data. A missing tool, failed query or nonexistent service is reported as a failed workflow.

`operation get-workflow` returns the instance's `WorkflowId`, `RootWorkflowId` and `ParentWorkflowId`, along with its state and result. The root points to itself and has no parent. Each subworkflow has its own ID. These inspection commands are authenticated and audited; they do not create a Lockbox.

`operation get-callstack` prints the selected instance and its descendants as a tree, including every full workflow ID, definition and state. Interactive output uses green for success, red for failure, blue for running and yellow for warnings. Redirected output preserves the tree and state names without terminal colors. Use `--output-format json` to obtain the full protocol response with parent and root IDs.

## Output formats

Commands default to human-readable tables. Single-object details use `Property` and `Value` columns; collections use one row per record. State, decision and risk values are shown by name. Structured results are expanded separately, and multiline output remains readable. Full IDs are preserved; wide collections fall back to individual property tables. `get-callstack` uses its colored tree in table mode. Redirected output has no added terminal colors.

Use `--output-format json` for the complete, unchanged AiurProtocol response, including pagination metadata. Use `--output-format csv` for business records with alphabetically ordered columns and quoted commas, quotes and line breaks. Nested values remain JSON in a cell; API numeric enums and full timestamps are preserved. Trace CSV contains event records, including column headers on an empty page; pagination and archive metadata are available in table and JSON output. Format names are case insensitive. Invalid formats are rejected before submitting any request.

```sh
dotnet ./bin/client/dms.dll operation get-workflow --id <workflow-id>
dotnet ./bin/client/dms.dll operation get-workflow --id <workflow-id> --output-format json
dotnet ./bin/client/dms.dll operation get-callstack --id <workflow-id> --output-format csv
```


See [typed subworkflows](subworkflows.md) for compiled registration, cross-machine calls, results, cancellation and recovery.

Standard workflow authors can follow [Adding a standard workflow](authoring-workflows.md), including compiled Machine/Cluster examples and typed SDK submissions.

For a typed aggregate across a frozen set of machines, see [Cluster disk usage](disk-usage.md).

## Structured investigation history

Approval and workflow state changes are automatically archived to ClickHouse through a transactional pending queue. See [structured workflow audit](workflow-audit.md) for attribution, deduplication and outage behavior. Existing trace commands continue to query trace text; no new investigation command is added in this phase.
