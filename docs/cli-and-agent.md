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

Deletion requires no machines, operation history or enrollment keys (including revoked keys). Its access grants are removed atomically and the audit record is retained.

## Install and join an Agent

An administrator with **Manage DMS access** issues a dedicated join key for one cluster:

```sh
dotnet ./bin/client/dms.dll access create-join-key \
  --cluster-id <cluster-id> --name 'Production enrollment' --days 7
```

The key is displayed once. It can enroll several machines into that cluster until it expires or is revoked. It can only submit registration requests; it cannot approve requests, query inventory or execute workflows. Its issuer needs permission to request the Medium registration workflow in that cluster. Registration uses the issuer's current request and automatic-approval permissions.

On each managed Linux x64 or arm64 host, run this once from Bash:

```sh
curl --fail --silent --show-error https://dms.example.com/agent/install.sh | \
  sudo bash -s -- --controller-url https://dms.example.com/ \
  --api-key '<join-key>' --cluster-name aiursoft-prod
```

The host needs `curl`, `tar`, systemd and access to the Controller. The Controller supplies a self-contained Agent, including its .NET runtime; the managed host needs neither internet access nor a preinstalled .NET runtime. Use HTTPS for production. The installer runs the Agent as root so approved operating-system workflows can execute. Add `--name server-01` to override the hostname.

The installer creates `/opt/aiursoft-dms-agent`, the `/usr/local/bin/dms-agent` symlink and `dms-agent.service`. It persists the private installation identity, enrollment receipt and machine credential under `/var/lib/aiursoft-dms-agent` (directory 0700, configuration 0600). It never persists the reusable join key or a human personal key. Keep this state directory when restarting or upgrading the Agent.

The join prints the enrollment, Lockbox, workflow and installation IDs. The service starts immediately and waits for approval without requiring another visit to the machine. An authorized approver can inspect and approve the request:

```sh
dotnet ./bin/client/dms.dll access list-enrollments --cluster-id <cluster-id>
dotnet ./bin/client/dms.dll lockbox get --id <lockbox-id>
dotnet ./bin/client/dms.dll lockbox approve \
  --id <lockbox-id> --reason 'Verified machine registration'
```

Self approval still requires the independent self-approval attribute. A machine credential becomes usable only after the registration workflow creates the machine. The Agent then connects outward over WebSocket. It requires no inbound SSH or database access.

If the Agent binary is already installed, the equivalent registration command is:

```sh
sudo dms-agent join --controller-url https://dms.example.com/ \
  --api-key '<join-key>' --cluster-name aiursoft-prod
```

Repeated joins resume the same pending request. A denied or unavailable request can be retried with a valid join key, producing a new request while preserving the installation identity. Once joined, repeating the installer preserves credentials and running tasks; it does not implicitly upgrade or restart an active Agent. Joining a different Controller or cluster is rejected while the existing identity remains in place.

Administrators can inspect or revoke join keys:

```sh
dotnet ./bin/client/dms.dll access list-join-keys --cluster-id <cluster-id>
dotnet ./bin/client/dms.dll access revoke-join-key --id <join-key-id>
```

Revocation prevents new registrations and activation of pending registrations. Previously approved machines retain their independent credentials. The enrollment history and approval decisions remain available for investigation.

### Upgrade an installed Agent

Submit an upgrade from the CLI:

```sh
dotnet ./bin/cli/dms.dll machine agent upgrade \
  --cluster-id <cluster-id> --machine-id <machine-id> \
  --reason "Install the Controller's Agent release"
```

This creates a **High** risk Lockbox. It freezes the selected machines and both architectures' exact release versions, archive digests and Agent assembly digests. Review and approve that Lockbox through the usual commands. The permission to approve your own request remains separate. If the Controller's release changes before approval, submit a new request; the approved operation never silently changes to a newer payload.

The installed Agent downloads its native self-contained bundle from its registered Controller, verifies the approved identity, pauses new task starts and waits up to one minute for running work to finish. Uncertain tasks must be reconciled first. A one-shot systemd unit installs the payload and restarts `dms-agent.service`. The machine keeps its installation identity, credential and task journal. It needs no internet connection or inbound SSH.

Follow the same Workflow ID using `operation get-workflow`, `get-callstack` and `get-trace`. Success is reported only by the matching new Agent over its authenticated Controller connection. A failed download leaves the old service running. An interrupted or unconfirmed switch requires verification and is never retried automatically. Cancellation after the handoff cannot roll back the switch. A child upgrade does not resume an interrupted parent's C# stack.

Use an installed Agent release that already supports this workflow for the first upgrade. Development Agents started from arbitrary publish directories cannot update themselves. This command does not provide scheduled updates, a historical version repository or automatic rollback. Hosts whose Agent cannot contact the Controller need an operator to restore connectivity.

### Build self-contained Agent assets

Controller Docker images and APKG packages include both Linux architectures. For a manual Controller publish, create the assets beside the Controller's published assembly:

```sh
bash scripts/build-agent-assets.sh ./bin/controller/agent-assets
```

The build requires the .NET 10 SDK and restores both architectures' runtime and host packs. The asset script explicitly ships serviced runtime patch 10.0.15, independently of the SDK's bundled runtime. Update that patch in the script when servicing the Agent runtime; admission and vulnerability checks still apply. The script also generates `releases.json` from the published assembly version and actual artifact digests. The Controller serves only the supported `linux-x64` and `linux-arm64` bundles. Its installation endpoints return 404 when those assets have not been packaged.

### Run an Agent during development

For an isolated development environment, register a machine with `cluster add-machine`, approve its Lockbox, then issue a machine key with `access issue-agent-key --machine-id <machine-id>`. Issuing another key replaces the previous one. Run the framework-dependent publish using a private persistent state directory:

```sh
export DMS_CONTROLLER=http://localhost:5000
export DMS_MACHINE_KEY='<agent-key>'
export DMS_STATE_PATH=/var/lib/dms-agent
dotnet /opt/dms-agent/Aiursoft.DMS.Agent.dll
```

This development mode requires a .NET 10 runtime. Preserve the installation identity in its state directory; a second installation with the same key is rejected until pending operations have been reconciled.

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
