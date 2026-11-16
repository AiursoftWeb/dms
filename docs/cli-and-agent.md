# CLI and Agent setup

[Back to README](../README.md)

The Controller provides the web UI and HTTP API. The CLI submits requests and inspects results; an Agent runs approved machine workflows on its own Linux host. The SDK is available in [Aiursoft.DMS.SDK](../src/Aiursoft.DMS.SDK).

## Install the CLI

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then install the global tool from NuGet:

```sh
dotnet tool install --global Aiursoft.DMS.Client
```

On AnduinOS or Ubuntu, add the global tool directory to your current Bash session's PATH:

```sh
export PATH="$HOME/.dotnet/tools:$PATH"
```

If your SDK installation has not already configured this directory, add that same line to `~/.bashrc` for new terminals. Check the installed command:

```sh
dms --help
```

Update or remove the CLI using the standard tool commands:

```sh
dotnet tool update --global Aiursoft.DMS.Client
dotnet tool uninstall --global Aiursoft.DMS.Client
```

Company environments can select `https://nupkg.aiursoft.com/v3/index.json` as their NuGet source. Normal installations use public NuGet. Installing the CLI does not install a Controller or register the laptop as an Agent.

## Build the clients from source

From the repository root, use the .NET 10 SDK to publish the CLI and Agent:

```sh
dotnet publish src/Aiursoft.DMS.Client -c Release \
  --no-self-contained -p:UseAppHost=false -o ./bin/client
dotnet publish src/Aiursoft.DMS.Agent -c Release \
  --no-self-contained -p:UseAppHost=false -o ./bin/agent
dotnet ./bin/client/dms.dll --help
```

These source publishes are framework-dependent applications. Install the .NET 10 runtime on each host that runs one, and distribute the entire publish directory. The CLI is also available as the global tool above. Agents use the Controller's self-contained installer below rather than a NuGet tool.

## Configure access

Run the interactive login once:

```sh
dms login
```

Enter your Controller endpoint (press Enter for `https://dms.aiursoft.com/`). Open the displayed **API Keys** link, sign in with your company account, create a personal key, then paste it into the hidden prompt. The CLI verifies the key with `access whoami` before saving it to `~/.DMS/auth.json`. A failed or cancelled login leaves the previous configuration unchanged. On Linux and macOS, the file is readable and writable only by its owner. It contains your personal key; keep it private.

Subsequent terminal sessions use the saved login automatically:

```sh
dms access whoami
dms cluster list
```

Run `dms login` again to change the saved Controller or key. `dms login --server https://dms.example.com/` skips the endpoint prompt. Delete `~/.DMS/auth.json` to remove the local login; revoke the key on the Controller's **API Keys** page to invalidate it.

For scripts, command options override environment variables, which override saved authentication:

```sh
export DMS_SERVER=https://dms.example.com/
read -rsp 'DMS personal API key: ' DMS_API_KEY
echo
export DMS_API_KEY
dms access whoami
```

`--server` and `--api-key` can also be supplied to individual commands. A saved key is used only for its verified Controller endpoint; an endpoint override pointing elsewhere requires its own key. Unset `DMS_SERVER` and `DMS_API_KEY` to return to the saved login. `whoami` shows the current user's ID, roles, five-risk permission matrix, effective self-approval capability and raw permissions. Table is the default; JSON and CSV are also supported. Machine keys authenticate Agents and cannot be used as personal login keys.

The examples below that use `dotnet ./bin/client/dms.dll` also work with `dms` after installing the global tool. Output formats and the Lockbox workflow apply equally to both forms.

### Manage clusters and permissions

Users with **Manage DMS access** can manage inventory and Agent identities through the CLI or **DMS access** web page. Configure workflow permissions in **Administration → Directory → Roles → Edit**. See [role permissions](permissions.md) for the five-risk matrix and upgrade requirements. Permissions apply to every machine in the instance; Cluster fields are inventory filters only.

```sh
dms access whoami
dms access whoami --output-format json
dms access whoami --output-format csv
dms cluster create --name aiursoft-prod
dms cluster list
dms access get
```

`access set-grant` and `access set-self-approval` have been removed. `access get` lists inventory and Agent identity metadata, without a second permissions configuration surface. Creating inventory or issuing a key never grants workflow execution privileges.

```sh
dotnet ./bin/client/dms.dll cluster rename --cluster-id <cluster-id> --name <new-name>
dotnet ./bin/client/dms.dll cluster drop --cluster-id <cluster-id>
```

Deletion requires no machines, operation history or enrollment keys (including revoked keys). The audit record is retained.

## Manage Docker containers

Docker commands use the selected machine's local Docker socket and the existing Lockbox approval flow. The Agent host needs Docker at `/usr/bin/docker`. Update both Controller and Agents before using new workflow definitions.

```sh
dotnet ./bin/client/dms.dll machine docker ps --grep-name nugetninja --reason "Find the bot container"
```

`--grep-name` matches literal text contained in the container name. Add `--all` to include stopped containers. The result contains full container IDs, names, images and status. Copy the complete ID into subsequent commands: names and short IDs are rejected so approval cannot accidentally target a replacement container.

```sh
dotnet ./bin/client/dms.dll machine docker logs --container <full-container-id> --tail 100 --reason "Read recent bot logs"
dotnet ./bin/client/dms.dll machine docker exec --container <full-container-id> --reason "Start the bot" -- /start.sh
dotnet ./bin/client/dms.dll machine docker kill --container <full-container-id> --reason "Stop this container"
```

Use `--cluster-id` and `--machine-id` when the target is not unique. Listing and logs require Low risk permission, execution requires VeryHigh, and kill requires High. Existing approval and self-approval settings apply; adding these commands grants no permissions. `exec` can run arbitrary commands with the container's configured user and can affect the host through mounted volumes. Review its arguments accordingly. `kill` sends SIGKILL to the container; Docker or Swarm may restart or replace it under its existing policy.

Logs default to the last 100 lines, accept `--tail` from 1 to 1000 and an optional `--since` timestamp with UTC offset. They do not follow indefinitely. Logs and command output are available through `operation get-trace`; they may contain application secrets, so grant access accordingly. Arguments after `--` are passed literally, without an implicit host shell. Do not put secrets in command arguments: approved inputs and output are recorded in the audit trail.

`exec` waits for completion and streams output. It does not detach or automatically retry. A disconnect, cancellation or failed Docker execution is marked **NeedsVerification** because the Docker client cannot prove whether the container command stopped; inspect the trace and application state before retrying. Killing a DMS workflow is not the same as killing the container. A successful exec confirms the command's exit, not the completion of any background work it started.

## Force a Box Puller mirror update

With `DMS_SERVER` and `DMS_API_KEY` configured, an enrolled Box can request:

```bash
# One accessible cluster with one active machine: refresh the main Puller inventory.
dms machine box puller force-update --reason "Refresh configured mirrors"

# Select one machine and image explicitly.
dms machine box puller force-update \
  --cluster-id "$CLUSTER_ID" --machine-id "$MACHINE_ID" \
  --image aiursoft/dms:latest --reason "Refresh DMS mirror"

# Refresh the CN Puller's configured inventory on the selected machine.
dms machine box puller force-update \
  --cluster-id "$CLUSTER_ID" --machine-id "$MACHINE_ID" \
  --puller cn --reason "Refresh configured China mirrors"
```

`--reason` remains required. `--puller` defaults to `main`; `--image` is optional.
Omitting the image refreshes the selected Puller's own configured inventory;
it does not discover every image in a registry. An explicitly empty or invalid
image is rejected. Add `--output-format json` or `csv` for structured output;
table remains the default.

For workflow submissions, `--cluster-id` may be omitted only when the caller
has exactly one accessible cluster. Machine targets may be omitted only when
the selected cluster has exactly one active (not disabled) machine; online status
does not determine selection. With multiple machines, use explicit
`--machine-id` values or intentionally request `--all-machines`. The Controller
resolves these defaults under its existing submission gate and stores the
resolved IDs before approval. Reusing the same `--request-id` preserves the
original resolved targets even if inventory changes.
An ambiguous or empty selection fails before creating a Lockbox.

This is the standard **High-risk** `machine.box.puller.force-update` workflow,
version 2. The caller needs `CanRequestHighRiskWorkflow`. Automatic approval also
requires `CanAutoApproveHighRiskWorkflow`; otherwise a reviewer needs
`CanApproveHighRiskWorkflow`. Approving your own request additionally requires
`CanApproveOwnWorkflow`. These are global role permissions; a role's name alone
does not authorize any action.

The Lockbox stores the actual cluster/machines, selected Puller and
`ImageScope: SingleImage` or `AllConfiguredImages` in canonical typed input.
The image scope must match `Image`; a contradictory scope is rejected. For the
all-images mode, approval covers the selected Puller's configured inventory at
execution, not a separately frozen list of image tags. For a fixed image scope,
pass `--image` explicitly. Child workflows inherit the approved scope.

Main selects the local Swarm `puller_downloader` service; CN selects
`puller_downloader_cn`. Exactly one running container must exist. Selecting CN
on a Box without CN fails at execution-time discovery, records Failed, and never
falls back to main. The Agent always uses `/usr/bin/docker` with the local
`unix:///var/run/docker.sock`, ignoring remote Docker context selection.

Main calls its existing `/app/entry.sh` with zero or one image arguments and
`SKIP_IF_SYNCED_HOURS=0`. Its existing Python runtime handles the DMS lock and
checks for a running scheduled entry. Global, HL and Proart main Puller sources
provide this interface. CN calls its updated `/app/force-update.sh` with zero or
one image arguments, reusing Bash/flock and cron's persistent `puller_cn.lock`.
Both CN quiet periods are disabled for this invocation; the cron schedule and
no-argument inventory are unchanged. Update the CN Puller image before using
this version's all-images mode; an unsupported old entry fails without syncing.
Registry configuration and credentials remain inside Puller.

Discovery and sync each appear as Process subworkflows with their own IDs and
streamed trace. Busy, unsupported and confirmed sync failures are Failed.
Cancellation or uncertain Docker exec outcome is NeedsVerification, with no
automatic replay. Success means mirror sync completed or was already current;
it does not force application or Shepherd deployment. Controller, CLI and Agent
must contain the new definition (Agent/APKG 1.0.18); the Box must already be
enrolled.

After submitting, query and approve the returned Lockbox ID, then use the
execution Workflow ID returned after approval:

```bash
dms lockbox get --id "$LOCKBOX_ID"
dms lockbox approve --id "$LOCKBOX_ID" --reason "Approve mirror refresh"
dms operation get-workflow --id "$WORKFLOW_ID"
dms operation get-callstack --id "$WORKFLOW_ID"
dms operation get-trace --id "$WORKFLOW_ID"
```

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

Use `dms operation list-definitions --cluster-id <cluster-id>` to see the Controller's registered public standard workflows and your permissions across the instance. The command supports `--output-format table|json|csv` and creates no Lockbox. The same catalog is available under **Standard workflows** in the web UI. See [workflow discovery](workflow-discovery.md) for permission and output details.

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
