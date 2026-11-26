# CLI and Agent setup

[Back to README](../README.md)

The Controller provides the web UI and HTTP API. The CLI submits requests and inspects results; an Agent runs approved machine workflows on its own Linux host. The SDK is available in [Aiursoft.DMS.SDK](../src/Aiursoft.DMS.SDK).

The [on-demand operations catalog](operations.md) documents filesystem, storage, performance, GPU, container, service, system, database, network and security workflows, including file artifacts and approval risks.

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

Run `dms` or a command group such as `dms access` to see the available commands. Use `--help` with a command to see its options and effects.

```sh
dms access
dms access issue-agent-key --help
dms machine filesystem rm --help
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

These source publishes are framework-dependent applications. Install the .NET 10 runtime on each host that runs one, and distribute the entire publish directory. Both clients are also available as .NET tools. Managed servers use the Controller's Tool installer below, which prepares the SDK and service setup.

## Configure access

Run the interactive login once:

```sh
dms login
```

Enter your Controller endpoint (press Enter for `https://dms.aiursoft.com/`). Open the displayed **API Keys** link, sign in with your company account, create a personal key, then paste it into the hidden prompt. The CLI verifies the key with `access whoami` before saving it to `~/.DMS/auth.json`. A failed or cancelled login leaves the previous configuration unchanged. On Linux and macOS, the file is readable and writable only by its owner. It contains your personal key; keep it private.

Subsequent terminal sessions use the saved login automatically:

```sh
dms access whoami
dms machine list
```

Run `dms login` again to change the saved Controller or key. `dms login --dms-server-endpoint https://dms.example.com/` skips the endpoint prompt. Delete `~/.DMS/auth.json` to remove the local login; revoke the key on the Controller's **API Keys** page to invalidate it.

For scripts, command options override environment variables, which override saved authentication:

```sh
export DMS_SERVER=https://dms.example.com/
read -rsp 'DMS personal API key: ' DMS_API_KEY
echo
export DMS_API_KEY
dms access whoami
```

`--dms-server-endpoint` and `--api-key` can also be supplied to individual commands. The endpoint is the DMS Controller URL. Machine commands take the managed machine's ID in `--machine-id`. A saved key is used only for its verified Controller endpoint; an endpoint override pointing elsewhere requires its own key. Unset `DMS_SERVER` and `DMS_API_KEY` to return to the saved login. `whoami` shows the current user's ID, roles, effective workflow permissions, effective self-approval capability and raw permissions. Table is the default; JSON and CSV are also supported. Machine keys authenticate Agents and cannot be used as personal login keys.

The examples below that use `dotnet ./bin/client/dms.dll` also work with `dms` after installing the global tool. Output formats and the Lockbox approval flow apply equally to both forms.

### Manage Agent access and permissions

Users with **Manage DMS access** can inspect inventory and manage Agent identities through the CLI or **DMS access** web page. Configure workflow permissions in **Administration → Directory → Roles → Edit**. See [role permissions](permissions.md) for the risk matrix and upgrade requirements. Permissions apply to every machine in the instance.

```sh
dms access whoami
dms access whoami --output-format json
dms access whoami --output-format csv
dms machine list
dms access get
```

`access set-grant` and `access set-self-approval` have been removed. `access get` lists inventory and Agent identity metadata, without a second permissions configuration surface. Issuing a key never grants workflow execution privileges. `machine list` is an Open workflow and prints its inventory result. `access get` is an authenticated administrative inspection.


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

Use `--machine-id` when the target is not unique. Listing and logs require Low risk permission, execution requires VeryHigh, and kill requires High. Existing approval and self-approval settings apply; adding these commands grants no permissions. `exec` can run arbitrary commands with the container's configured user and can affect the host through mounted volumes. Review its arguments accordingly. `kill` sends SIGKILL to the container; Docker or Swarm may restart or replace it under its existing policy.

Logs default to the last 100 lines, accept `--tail` from 1 to 1000 and an optional `--since` timestamp with UTC offset. They do not follow indefinitely. Logs and command output are available through `workflow trace`; they may contain application secrets, so grant access accordingly. Arguments after `--` are passed literally, without an implicit host shell. Do not put secrets in command arguments: approved inputs and output are recorded in the audit trail.

`exec` waits for completion and streams output. It does not detach or automatically retry. A disconnect, cancellation or failed Docker execution is marked **NeedsVerification** because the Docker client cannot prove whether the container command stopped; inspect the trace and application state before retrying. Killing a DMS workflow is not the same as killing the container. A successful exec confirms the command's exit, not the completion of any background work it started.

## Inspect disk pressure

```sh
dms machine layout df --machine-id "$MACHINE_ID" --reason "Check local filesystem capacity"
dms machine filesystem du --machine-id "$MACHINE_ID" --path /swarm-vol --depth 1 --reason "Locate large directories"
dms machine docker system df --machine-id "$MACHINE_ID" --reason "Inspect Docker storage"
```

These are standard workflows; submissions return `LockboxId` and nullable `WorkflowId`. Use
`workflow callstack` to find their Process children and `workflow trace`
for output. `layout df` is Safe and excludes overlay, tmpfs, devtmpfs, squashfs
and efivarfs mounts. It streams local filesystem rows to trace instead of storing
the report in the workflow result. Directory usage and Docker disk usage are Low;
they require `CanRequestLowRiskWorkflow`, automatic approval additionally requires
`CanAutoApproveLowRiskWorkflow`, and a reviewer needs `CanApproveLowRiskWorkflow`.

`filesystem du` reports allocated bytes, accepts an absolute `--path` and a
`--depth` from 0 to 3 (default 1). It stays on the operand's filesystem, does not
follow symbolic links or read file contents, runs with low CPU/IO priority, and
stops after 60 seconds. A failed, timed-out or cancelled scan is partial; inspect
its trace before drawing conclusions. Docker's report includes images,
containers, local volumes and build cache with estimated reclaimable space.
Neither command removes data or automatically starts cleanup.

## Remove an approved path

```sh
dms machine filesystem rm --machine-id "$MACHINE_ID" --path /tmp/obsolete-file --reason "Remove the reviewed file"
dms machine filesystem rm --machine-id "$MACHINE_ID" --path /tmp/obsolete-directory --recursive --reason "Remove the reviewed directory tree"
```

`filesystem rm` is a standard **High** risk workflow. Request permission is
`CanRequestHighRiskWorkflow`, automatic approval additionally requires
`CanAutoApproveHighRiskWorkflow`, and reviewers need `CanApproveHighRiskWorkflow`.
Self-review additionally needs `CanApproveOwnWorkflow`. The request records the
machine, exact path and recursive flag. Submission returns `LockboxId` and nullable `WorkflowId`;
inspect callstack and trace for execution and removed entries.

One normalized absolute path is required. There is no shell, wildcard expansion,
force option or implicit recursion. Root and trailing slashes are rejected.
Symlink parents are rejected at preflight; a symlink at the target is removed
without following it. Recursive removal uses GNU rm's one-filesystem and
preserve-root protections: different-device mounts are not traversed, and an
operand on a different device from its parent is refused. Same-device bind mounts
are not isolated by these flags. Approval fixes the path, not its future contents;
ensure nobody replaces parent directories or writes new data there during removal.

Deletion is permanent and creates no backup. Missing paths or unapproved directory
recursion fail before starting rm. A nonzero rm exit or cancellation after dispatch
is **NeedsVerification** because deletion may be partial. Inspect the path before
submitting another request; the workflow does not automatically retry.

Update the Controller, CLI (1.2.3) and target Agent (1.3.3) before using removal.

## Configure the machine's Box Git identity

```sh
dms machine box set-git-ssh-identity --machine-id "$MACHINE_ID" \
  --identity-file /home/anduin/.ssh/id_ed25519 \
  --known-hosts-file /home/anduin/.ssh/known_hosts \
  --reason "Use this machine's existing read-only Box deployment identity"
```

`configure-git` is accepted as an alias. This Medium standard workflow records
references to two existing machine-local files in
`/etc/aiursoft-dms-agent/box-git.json` (0600). The directory is private
when created. Key contents are never uploaded, copied, returned or traced.
Use the Box's read-only deployment key and a file containing independently
verified GitLab host keys. A concrete normalized absolute path containing only
ASCII letters, digits, slash, dot, underscore or hyphen is required. Missing,
linked or insecurely permissioned files are rejected. The identity file must not
be readable or writable by group/others; the trust file must not be writable by
group/others. No key or trust entry is generated or accepted automatically.

Subsequent `box update-only` and `box deploy` workflows use this identity in the Agent's root
context, with strict host checking, noninteractive authentication and SSH agent
identities disabled. Missing/mismatched host trust or rejected credentials fail
the Git pull before deployment starts. The Git origin is retained. Machines
without this optional configuration continue to use their existing Git setup.
Use `box update-only` to verify GitLab access after configuring the identity.

The `set-git-ssh-identity` name is available in CLI 1.29.0. Use a Controller
and target Agent that support `machine.box.configure-git` (Agent 1.3.2 or later).

## Manage Box secrets

These commands use the selected Box's existing private `.env.prod` on the Agent. Submit
each request for one machine, with an audit reason and a key for single-key
operations.

```bash
dms machine box list-secret --machine-id <id> --reason "Inventory Box secrets"
dms machine box get-secret-summary --machine-id <id> --name CLOUDFLARE_API_TOKEN --reason "Check rotation"
dms machine box set-secret --machine-id <id> --name CLOUDFLARE_API_TOKEN --reason "Rotate ACME credential"
dms machine box get-secret-raw --machine-id <id> --name CLOUDFLARE_API_TOKEN --reason "Recover the approved credential"
dms machine box get-secret-raw --reveal lb/<id>
```

List and summary are Low risk. Use `--result lb/<id>` on either command to read
the completed result after approval. The summary fingerprint is keyed by the
Agent and identifies changes to the same key on that installation.

Set and raw are VeryHigh risk. Set prompts without echo or accepts one line via
`--stdin` through HTTPS. The Controller holds the staged value encrypted in
memory for up to 30 minutes. The approved Agent consumes it once and updates the
file atomically under the Box deployment lock. Use the standard Box deployment
workflow to apply the configuration.

Raw always requires a different human approver. After the workflow succeeds,
the original requester has five minutes from delivery to reveal the exact value
once over HTTPS. Plaintext is returned to that CLI's standard output. Lockbox,
workflow and audit records contain key names and metadata. A Controller restart
clears pending secret transfers. An expired or consumed transfer requires a new
request.

Generic file reads, copies and exports refuse the Box private configuration,
including recursive transfers containing it.

## Inspect a Box

```sh
dms machine box inspect --machine-id "$MACHINE_ID" --reason "Check Box before deployment"
```

This is the standard **Safe** `machine.box.inspect` workflow. It creates a
Lockbox, uses the existing Safe request/automatic-approval/reviewer permissions,
and executes only after approval. All workflow-submitting CLI commands return
only `LockboxId` and nullable `WorkflowId` (also with JSON or CSV output). The
Lockbox ID always has the form `lb/<UUID>`. Pending requests have no Workflow;
automatic approval returns the newly created execution root ID. For manual
approval, inspect the Lockbox again after the decision to obtain its Workflow ID:

```sh
dms lockbox list --decision Pending
dms lockbox get --id "lb/<UUID>"
dms lockbox approve --id "lb/<UUID>" --reason "Reviewed scope"
# Alternatively: dms lockbox deny --id "lb/<UUID>" --reason "Rejected scope"
```

Any authenticated user can review past activity without special permission:
`dms lockbox history` lists Lockbox lifecycle events and `dms workflow history`
lists workflow executions, each filtered by `--requester`/`--approver` (name or
ID), `--machine` (name or ID), `--definition`, `--state`/`--decision`, `--risk`,
`--origin`/`--trigger`, `--lockbox-id`, `--workflow-id` and a `--from`/`--until`
window that defaults to the last 7 days. See [workflow audit](workflow-audit.md)
for the full filter reference and the permission-gated raw query.

Then inspect the actual execution tree:

```sh
dms workflow status "$WORKFLOW_ID"
dms workflow callstack "$WORKFLOW_ID"
dms workflow trace "$PROCESS_WORKFLOW_ID"
```

Use the callstack to find the inspection's Process child ID. Its trace contains
a structured summary report covering the repository, branch, commit, tracked/staged-change
flags, whether `.env.prod` is tracked, sanitized origin metadata, configured stages and stacks,
running Swarm stacks alignment, service replica health, and secrets count. Raw origin configuration,
usernames, passwords, URL queries and fragments are never printed. Unknown hosts
and repository paths are redacted; `official` reports whether an individual origin
matches the deploy allowlist. Multiple origins still prevent deployment even if
each individual address is official.

Inspection only reads the unique regular Box checkout directly under `/swarm-vol`.
It does not pull, deploy, create a deployment lock, read environment-file contents
or private keys, or establish an SSH connection. This is a point-in-time snapshot;
deployment performs its own fresh preflight. Inspection details live in trace,
not in the submission receipt or workflow result. Update Controller and Agents
before using the new definition.

## Disable SSH on one machine

```sh
dms machine ssh disable --machine-id "$MACHINE_ID" --reason "Close remote SSH access"
```

This is a medium-risk Lockbox request. The target Agent masks and stops the
system OpenSSH service and socket. When a DMS SSH lease exists, the workflow
terminates its sessions and removes the lease's temporary account, home, sudo
grant and tracked firewall rules. Ordinary SSH sessions may outlive the system
service. The Agent checks for both remaining listeners and established OpenSSH
connections before reporting success.

Use `dms workflow status <WorkflowId>` or the workflow details page to see the
closure result. A run marked NeedsVerification can show separate listener
and connection counts and a sample of the endpoints and processes found.
Open the execution log for the last completed step and its diagnostics.
A new approved disable can verify closure after the remaining listener or
connection is gone.

## Open a temporary SSH lease

Open the target's **SSH management** section from **DMS → Machines**. It shows
observed access, temporary lease readiness, and links to SSH workflow
history. Expand **Request Disable SSH** to submit the existing Medium risk
Lockbox request from the machine page when DMS observes ordinary access or
an unverified baseline. A temporary SSH lease requires verified closure.
Verify at least one personal key on **SSH public keys**, then submit a
very-high-risk request for one machine.

```sh
dms machine ssh temp-allow-ssh --machine-id "$MACHINE_ID" \
  --minutes 30 --reason "Temporary maintenance access"
```

By default, the request includes all of your verified, unrevoked keys at the
time of submission. Add `--key-id "$KEY_ID"` to select one verified key.
The Lockbox records the key fingerprints, target, duration and sudo choice.
The Agent rechecks every frozen key with the Controller at execution. A key
registered later cannot enter that lease, and revoking a selected key before
execution requires a new request. The temporary account accepts the frozen
public keys. Use `--sudo` only when the approved request needs passwordless sudo.
The default duration is 30 minutes, with a supported range of 30 to 270 minutes.
Update the Controller and Agent before using the default key selection.

Get the connection username, port and UTC deadline from the machine
workflow's result or trace. The network and firewall must already permit
access to the target's SSH port. Each machine can host one lease. Run
`dms machine ssh disable` to end it early.

The Agent checks its recorded lease at startup and every five minutes.
It can close an expired lease while disconnected from the Controller and
send the trace after reconnecting. Allow for the polling interval when
estimating when sessions will disconnect. Update the Controller before
upgrading Agents to this release.

### Schedule security baseline application

In **Administration → Global Settings**, configure **Scheduled Security Baseline** with the IDs of the machines to include and the desired policy.

```json
{
  "machineIds": ["f271c4e3-b328-41de-bef7-1b031bf716d2"],
  "policy": {
    "version": "maintenance-v1",
    "requireFail2ban": true,
    "ufwEnabled": false
  }
}
```

The Agent runs `machine.security.apply-baseline` daily at 03:00 UTC.
Only machines explicitly listed in the Controller setting apply the baseline.
An empty setting or machine list disables enforcement. The Controller must be reachable to supply the current policy.

The workflow closes ordinary OpenSSH when there is no temporary lease.
A healthy, unexpired lease remains usable, and the existing five-minute lease
check still handles its expiry. Firewall changes are deferred while a lease
is present. Setting `requireFail2ban` installs the package if missing.
Omit `ufwEnabled` to leave UFW as it is. Setting it to `true` enables UFW
with the host's existing rules. Configure those rules before enabling it.
Setting it to `false` disables UFW. After a change, the Agent checks the
state and its Controller connection, and attempts to restore the prior
state if verification fails. Secure Boot, encryption and CleanTraffic
remain part of the separate read-only audit.

Automatic runs and the SSH lease-check child appear in workflow history.
Update the Controller before upgrading Agents to this release.

## Deploy a Box

```bash
dms machine box update-only --machine-id "$MACHINE_ID" \
  --reason "Sync the merged Box configuration ahead of maintenance"
```

`box update-only` is a **Medium-risk** workflow that fast-forwards the local
checkout and reports the previous and current commits. It uses the same checkout
checks, Git identity and machine lock as deployment. It leaves `.env.prod`
in place and skips `deploy.sh`. Use CLI 1.32.0 and Agent 1.34.0 or later
with a Controller that lists `machine.box.update-only`.

```bash
dms machine box deploy --machine-id "$MACHINE_ID" \
  --stage 4 --filter dms --reason "Deploy merged DMS configuration"
```

This is the standard **High-risk** `machine.box.deploy` workflow. A colleague
with `CanRequestHighRiskWorkflow` can apply; without automatic approval the
request waits for someone with `CanApproveHighRiskWorkflow`. Automatic approval
and self-approval use the existing independent permissions. The command does
not grant permissions or require a particular role name.

`--stage` defaults to `4` and accepts `0` through `4`. It is a **starting**
stage: all later stages run too. `--filter` is an optional literal substring
of stage-4 stack names; omitting it selects all stage-4 stacks. Earlier stages
and shared preparation are not filtered. Stages 0–3 can change host packages,
Docker, networking and shared infrastructure. Stage 4 also performs the Box's
shared preparation. `--reason` is required; machine selection, output formats
and `--request-id` follow the normal submission rules.

The Agent looks for exactly one regular Box Git checkout directly under
`/swarm-vol`, with a directory name starting with `boxfor`, a `.git` directory and a regular
`deploy.sh`. Missing or ambiguous checkouts fail before updating anything. The
checkout must use exactly one official Aiursoft GitLab Box origin and its `master`
branch, with no tracked local changes. Supported origins are HTTPS on
`gitlab.aiursoft.com`, the existing `git@gitlab.aiursoft.com:aiursoft/...` form,
and GitLab's published `ssh://git@ssh.aiursoft.com:2202/aiursoft/...` endpoint.
The origin must point to an Aiursoft repository whose name starts with `boxfor`.
Embedded credentials, other
hosts, ports, namespaces and multiple origins are rejected. Both workflows fetch
remote `master` and fast-forward to the fetched commit. They reject a tracked
`.env.prod` in the local checkout or incoming commit before changing the working
tree. Deployment then executes `deploy.sh` with separate arguments.
The approval covers the latest master at execution time. The result and trace
record the previous and deployed commit IDs.

A shared machine lock prevents concurrent DMS Box updates and deployments. Git validation, update,
commit inspection and deployment are Process subworkflows with independent
IDs and streamed logs. A nonzero script exit is Failed and can leave partial
changes. Cancellation during deployment is NeedsVerification because submitted
Docker or host changes can continue. Interrupted work is never automatically
replayed. Inspect the trace and current service state before submitting another
request.

Success confirms that the deployment script exited zero; the result explicitly
reports `HealthVerified: false`. Check the selected Swarm services and application
health separately. Updating the Controller itself can temporarily interrupt the
CLI/API connection; inspect the original Workflow ID after reconnecting. Update
the Controller, CLI (1.2.0) and Agent (1.3.0) before using this new definition.

## Inspect FRPS

Use `dms machine frps status` on managed Linux machines with systemd.

```sh
dms machine frps status --machine-id <machine-id> --reason "Check FRPS health" --output-format Json
```

The Low risk workflow reports standard `frps.service` and the supported encrypted `turn.service` layout. It returns the installed and running versions when they can be verified, systemd state and startup time, automatic restart count, process ID, local TCP and UDP listeners, and bounded journal counts. The journal summary covers up to 250 entries from the last hour. A full sample can omit earlier entries. The report includes configuration paths and presence without exposing configuration contents, authentication tokens, remote connections, or journal messages. It also reports unavailable observations and returns an empty service list on machines without either supported service.

The Controller and target Agent must both support the workflow. Approval follows the existing Low risk policy for the requesting account. Use the receipt's workflow ID to read the report once execution finishes.

```sh
dms workflow callstack <workflow-id> --output-format Json
```

## Deploy FRPS

Use `dms machine frps deploy` with one managed Linux machine. The request opens a Very High risk Lockbox. The Agent installs the pinned upstream FRPS release for AMD64 or ARM64, verifies its SHA-256 and configuration, and checks that the systemd service starts. The Agent gives GitHub up to 40 minutes to deliver the complete archive. If the download times out or fails, it tries the same public release through GH-Proxy for up to five minutes. Both sources must match the pinned SHA-256 before installation, and a checksum mismatch stops deployment.

```bash
dms machine frps deploy --machine-id <machine-id> --reason "Upgrade FRPS"
```

For a fresh install, supply a nonempty `--frps-token`:

```bash
read -rs frps_token
dms machine frps deploy --machine-id <machine-id> --frps-token "$frps_token" --reason "Install FRPS"
unset frps_token
```

A fresh install creates `frps.service` and listens on TCP and UDP port 7000. An upgrade of an existing `frps.service` or supported running `turn.service` installation preserves the service definition and configuration when the token option is omitted or empty. To rotate an existing inline TOML token, supply the replacement. The Controller encrypts the token before saving the Lockbox and releases it only to the approved running Agent. An upgrade restarts FRPS and briefly interrupts its tunnels. The supported wrapper retains its encrypted configuration, including when rotating the token. External authentication requires separate maintenance.

## Force a Box Puller mirror update

With `DMS_SERVER` and `DMS_API_KEY` configured, an enrolled Box can request:

```bash
# One active machine: refresh the main Puller inventory.
dms machine box puller force-update --reason "Refresh configured mirrors"

# Select one machine and image explicitly.
dms machine box puller force-update \
  --machine-id "$MACHINE_ID" \
  --image aiursoft/dms:latest --reason "Refresh DMS mirror"

# Refresh the CN Puller's configured inventory on the selected machine.
dms machine box puller force-update \
  --machine-id "$MACHINE_ID" \
  --puller cn --reason "Refresh configured China mirrors"
```

`--reason` remains required. `--puller` defaults to `main`; `--image` is optional.
Omitting the image refreshes the selected Puller's own configured inventory;
it does not discover every image in a registry. An explicitly empty or invalid
image is rejected. Add `--output-format json` or `csv` for structured output;
table remains the default.

For workflow submissions, machine targets may be omitted only when this
Controller has exactly one active (not disabled) machine; online status
does not determine selection. With multiple machines, use explicit
`--machine-id` values or intentionally request `--all-machines`. The Controller
resolves these defaults under its existing submission gate and stores the
resolved IDs before approval. Reusing the same `--request-id` preserves the
original resolved targets even if inventory changes.
An ambiguous or empty selection fails before creating a Lockbox.

This is the standard **Medium-risk** `machine.box.puller.force-update` workflow,
version 2. The caller needs `CanRequestMediumRiskWorkflow`. Automatic approval
requires `CanAutoApproveMediumRiskWorkflow`. Manual approval requires
`CanApproveMediumRiskWorkflow`. Approving your own request additionally requires
`CanApproveOwnWorkflow`. Submit a new request if a legacy High-risk Lockbox
was still pending when the Controller changed the risk level.

The Lockbox stores the actual machines, selected Puller and
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
dms workflow status "$WORKFLOW_ID"
dms workflow callstack "$WORKFLOW_ID"
dms workflow trace "$WORKFLOW_ID"
```

## Install and join an Agent

An administrator with **Manage DMS access** issues a dedicated join key for this Controller:

```sh
dms access create-join-key \
  --name 'Production enrollment' --days 7
```

The key is displayed once. It can enroll several machines into this Controller until it expires or is revoked. It can only submit registration requests; it cannot approve requests, query inventory or execute workflows. Its issuer needs permission to request the Medium registration workflow. Registration uses the issuer's current request and automatic-approval permissions.

On each managed Ubuntu or AnduinOS x64 or arm64 host, run this once from Bash:

```sh
curl --fail --silent --show-error https://dms.example.com/agent/install.sh | \
  sudo bash -s -- --api-key '<join-key>'
```

The host needs `curl`, systemd and access to the Controller. The installer uses `apt-get update` and `apt-get install -y dotnet10` if the .NET 10 SDK is missing; the configured APT source may be an internal mirror. It then downloads the bundled NuGet Tool package from the Controller and installs it with `dotnet tool update --tool-path`, using only a temporary local package source. No public NuGet access is needed. The script includes its Controller origin; `--controller-url` can override it. After initial SDK setup, Agent operation and approved Tool upgrades only require access to the Controller. Use HTTPS for production. The installer runs the Agent as root so approved operating-system workflows can execute. Add `--name server-01` to override the hostname.

The installer keeps versioned Tool installations under `/opt/aiursoft-dms-agent/tools`, the active `/opt/aiursoft-dms-agent/dms-agent` link, the `/usr/local/bin/dms-agent` symlink and `dms-agent.service`. It persists the private installation identity, enrollment receipt and machine credential under `/var/lib/aiursoft-dms-agent` (directory 0700, configuration 0600). It never persists the reusable join key or a human personal key. Keep this state directory when restarting or upgrading the Agent.

The join prints the enrollment, prefixed Lockbox and installation IDs, plus a Workflow ID only once execution has been approved. The service starts immediately and waits for approval without requiring another visit to the machine. An authorized approver can inspect and approve the request:

```sh
dms access list-enrollments
dms lockbox get --id <lockbox-id>
dms lockbox approve \
  --id <lockbox-id> --reason 'Verified machine registration'
```

Self approval still requires the independent self-approval attribute. A machine credential becomes usable only after the registration workflow creates the machine. The Agent then connects outward over WebSocket. It uses a 15-second WebSocket keep-alive interval and reconnects if the peer does not answer a ping within 15 seconds. It requires no inbound SSH or database access.

If the Agent binary is already installed, the equivalent registration command is:

```sh
sudo dms-agent join --controller-url https://dms.example.com/ \
  --api-key '<join-key>'
```

For foreground development on a machine with the .NET 10 SDK, Agent version 1.1.1 and later can also be installed as a NuGet tool:

```sh
dotnet tool install --global Aiursoft.DMS.Agent
dms-agent --help
dms-agent join --controller-url https://dms.example.com/ \
  --api-key '<join-key>' --state-path "$HOME/.local/state/aiursoft-dms-agent"
dms-agent run --state-path "$HOME/.local/state/aiursoft-dms-agent"
```

The foreground Agent waits for the same registration approval and uses the same persistent identity and task journal. It runs workflows as the current operating-system user. This installation does not register a service. Use the Controller installer above for managed servers and the approved upgrade workflow below for their lifecycle; that path uses the Controller package and requires no public NuGet access on the managed host. Do not add a scheduled `dotnet tool update` job to managed servers. Remove a development tool with `dotnet tool uninstall --global Aiursoft.DMS.Agent`.

Repeated joins resume the same pending request. A denied or unavailable request can be retried with a valid join key, producing a new request while preserving the installation identity. Once joined, repeating the installer preserves credentials and running tasks; it does not implicitly upgrade or restart an active Agent. Joining a different Controller is rejected while the existing identity remains in place.

Administrators can inspect or revoke join keys:

```sh
dms access list-join-keys
dms access revoke-join-key --id <join-key-id>
```

Revocation prevents new registrations and activation of pending registrations. Previously approved machines retain their independent credentials. The enrollment history and approval decisions remain available for investigation.

### Recover an existing Agent connection key

Use this procedure if an enrolled Agent's connection key is lost or exposed and its installation state is still available. An administrator with **Manage DMS access** needs access to the machine independently of DMS.

1. Use `dms access get` to find the machine ID, then open the machine's console or another independent connection. Issuing a key immediately invalidates the old key, and the Agent needs the new one to connect.
2. From the administrator's DMS CLI, request the replacement key.

   ```sh
   dms access issue-agent-key --machine-id <machine-id>
   ```

3. On the machine, open `/var/lib/aiursoft-dms-agent/connection.json` with `sudoedit` and set `AgentKey` to the returned key. Use the configured state directory if the service uses a custom path. Restart the service, then have an operator with **Read DMS operations** verify under **Machines** that the last seen time updates.

   ```sh
   sudo systemctl restart dms-agent.service
   ```

For a rebuilt host with a fresh state directory, follow the installation and approval steps above. Approval creates a new machine ID. A fresh enrollment leaves the previous credential valid until an administrator replaces it separately.

### Upgrade an installed Agent

Submit an upgrade from the CLI:

```sh
dms machine agent upgrade \
  --machine-id <machine-id> \
  --reason "Install the Controller's Agent release"
```

This creates a **Medium** risk Lockbox. The role must grant Medium request permission. It can run automatically with Medium auto-approval permission, or wait for a reviewer with Medium approval permission. Self-approval also requires its separate permission. The Lockbox freezes the selected machines, versions and digests. Submit a new request when the Controller release changes before approval or when replacing a pending High-risk request.

The installed Agent downloads its architecture-specific upgrade bundle from its registered Controller, verifies the approved identity, pauses new task starts and waits up to one minute for running work to finish. Uncertain tasks must be reconciled first. The bundle includes an independent native helper, the Tool package and its package/assembly digests, all covered by the approved archive digest. A one-shot systemd unit installs the Tool from that local package, verifies the installed assembly, switches the service executable and restarts `dms-agent.service`. The machine keeps its installation identity, credential and task journal. Existing native services migrate through the same approved workflow; if they lack the .NET 10 SDK, the helper prepares `dotnet10` through APT before stopping the old service. Later Tool upgrades use only the Controller and the installed SDK, with no inbound SSH or public NuGet connection.

Follow the same Workflow ID using `workflow status`, `workflow callstack` and `workflow trace`. Success is reported only by the matching new Agent over its authenticated Controller connection. A failed download leaves the old service running. An interrupted or unconfirmed switch requires verification and is never retried automatically. Cancellation after the handoff cannot roll back the switch. A child upgrade does not resume an interrupted parent's C# stack.

Use an installed Agent release that already supports this workflow for the first upgrade. Development Agents started from arbitrary publish directories cannot update themselves. The manual command does not provide a historical version repository or automatic rollback. Hosts whose Agent cannot contact the Controller need an operator to restore connectivity.

### Schedule Agent upgrades

Every managed machine running an installed Agent with this feature runs `machine.agent.scheduled-upgrade` at 00:00, 08:00 and 16:00 UTC. The Agent authenticates to the Controller and checks for a strictly newer version for its architecture. While it waits, other workflows run normally. New work resets the 10-second idle window. Once the window completes, the Agent reserves maintenance, checks the release again, and uses the same verified activation and reconnect confirmation as a manual upgrade. On startup or after scheduling resumes, the Agent checks the latest missed occurrence once. It retries temporary failures reading the release manifest, saving the chosen release before activation.

Open **Workflow history** to inspect the scheduled run and its outcome. Use the manual command above to bootstrap an older Agent onto a release that supports this schedule or to request an immediate upgrade through Lockbox.

### Build Agent installation and upgrade assets

Controller Docker images and APKG packages include both Linux architectures. For a manual Controller publish, create the assets beside the Controller's published assembly:

```sh
bash scripts/build-agent-assets.sh ./bin/controller/agent-assets
```

The build requires the .NET 10 SDK and restores both architectures' runtime and host packs. The asset script explicitly ships serviced runtime patch 10.0.15, independently of the SDK's bundled runtime. Update that patch in the script when servicing the Agent runtime; admission and vulnerability checks still apply. The script also packs the standard NuGet Tool, embeds its package version and digest in the installer, and generates `releases.json` from the published assembly version and actual artifact digests. The Controller serves the Tool package at `/agent/package` and only the supported `linux-x64` and `linux-arm64` upgrade bundles. Its installation endpoints return 404 when those assets have not been packaged.

### Run an Agent during development

For an isolated development environment, register a machine with `machine add`, approve its Lockbox, then issue a machine key with `access issue-agent-key --machine-id <machine-id>`. Issuing another key replaces the previous one. Run the framework-dependent publish using a private persistent state directory:

```sh
export DMS_CONTROLLER=http://localhost:5000
export DMS_MACHINE_KEY='<agent-key>'
export DMS_STATE_PATH=/var/lib/dms-agent
dotnet /opt/dms-agent/Aiursoft.DMS.Agent.dll
```

This development mode requires a .NET 10 runtime. Preserve the installation identity in its state directory; a second installation with the same key is rejected until pending operations have been reconciled.

## Discover standard workflows

Use `dms operation list-definitions` to see the Controller's registered public standard workflows and your permissions across the instance. The command supports `--output-format table|json|csv` and creates no Lockbox. The same catalog is available under **Standard workflows** in the web UI. See [workflow discovery](workflow-discovery.md) for permission and output details.

## Request and inspect operations

```sh
dotnet ./bin/client/dms.dll machine list
dotnet ./bin/client/dms.dll machine performance info \
  --machine-id <machine-id> --reason 'Inspect capacity'
dotnet ./bin/client/dms.dll machine agent status \
  --machine-id <machine-id> --reason 'Inspect Agent'
dotnet ./bin/client/dms.dll machine systemctl status \
  --machine-id <machine-id> \
  --service example.service --reason 'Inspect service'
dotnet ./bin/client/dms.dll machine layout lsblk \
  --machine-id <machine-id> --reason 'Inspect block devices'
dotnet ./bin/client/dms.dll machine layout df \
  --machine-id <machine-id> --reason 'Inspect local capacity'
dotnet ./bin/client/dms.dll machine systemctl restart \
  --machine-id <machine-id> \
  --service example.service --reason 'Approved maintenance'
dotnet ./bin/client/dms.dll workflow status <workflow-id>
dotnet ./bin/client/dms.dll workflow callstack <workflow-id>
dotnet ./bin/client/dms.dll workflow trace <workflow-id>
```

Each operational command creates a Lockbox before execution. A Lockbox is a separate approval record and is never itself a new Workflow. Before approval or after denial its WorkflowId is null. Approval creates exactly one execution root; repeated decisions cannot create another. An execution failure leaves the original Approved decision intact. Inspection and `lockbox approve`/`deny` use authenticated management APIs without creating another Lockbox.

Use `--all-machines` instead of `--machine-id` to freeze all currently active machines into a request. Use `--request-id` when retrying the same submission and `--output-format json` for automation.

`operation kill-workflow --id <workflow-id> --reason 'Stop operation'` creates a Medium-risk Lockbox. Cancellation requests cooperative termination; it does not roll back completed side effects. See [recovery and cancellation](./configuration.md#recovery-and-cancellation). With the updated Controller and CLI, both `machine add` and `operation kill-workflow` submit typed inputs. Existing clients and pending requests using the previous top-level machine name or workflow ID remain supported.

Agent status, performance information, `layout lsblk` and `layout df` use the Safe risk policy; `systemctl status` uses Low and restart uses Medium. Service commands require a concrete `.service` name. Status reads fixed systemd properties without reading the journal. Block-device queries exclude loop devices, and capacity queries cover local filesystems. No extra command arguments or shell expressions are accepted.

Read-only process results contain `ExitCode`, `Output` and `ErrorOutput`; the output is also available in the workflow trace. Each output stream is limited to 8192 characters, and the serialized result to 24000 characters. Larger queries fail explicitly rather than returning truncated data. A missing tool, failed query or nonexistent service is reported as a failed workflow.

`workflow status` returns the instance's `WorkflowId`, `RootWorkflowId` and `ParentWorkflowId`, along with its state and result. The root points to itself and has no parent. Each subworkflow has its own ID. These inspection commands are authenticated and audited; they do not create a Lockbox.

`workflow callstack` prints the selected instance and its descendants as a tree, including every full workflow ID, definition and state. Interactive output uses green for success, red for failure, blue for running and yellow for warnings. Redirected output preserves the tree and state names without terminal colors. Use `--output-format json` to obtain the full protocol response with parent and root IDs.

## Output formats

Commands default to human-readable tables. Single-object details use `Property` and `Value` columns; collections use one row per record. State, decision and risk values are shown by name. Structured results are expanded separately, and multiline output remains readable. Full IDs are preserved; wide collections fall back to individual property tables. `workflow callstack` uses its colored tree in table mode. Redirected output has no added terminal colors.

Use `--output-format json` for the complete, unchanged AiurProtocol response, including pagination metadata. Use `--output-format csv` for business records with alphabetically ordered columns and quoted commas, quotes and line breaks. Nested values remain JSON in a cell; API numeric enums and full timestamps are preserved. Trace CSV contains event records, including column headers on an empty page; pagination and archive metadata are available in table and JSON output. Format names are case insensitive. Invalid formats are rejected before submitting any request.

```sh
dotnet ./bin/client/dms.dll workflow status <workflow-id>
dotnet ./bin/client/dms.dll workflow status <workflow-id> --output-format json
dotnet ./bin/client/dms.dll workflow callstack <workflow-id> --output-format csv
```


See [typed subworkflows](subworkflows.md) for compiled registration, cross-machine calls, results, cancellation and recovery.

Standard workflow authors can follow [Adding a standard workflow](authoring-workflows.md), including compiled Machine/Cluster examples and typed SDK submissions.

For a typed aggregate across a frozen set of machines, see [Cluster disk usage](disk-usage.md).

## Structured investigation history

Approval and workflow state changes are automatically archived to ClickHouse through a transactional pending queue. See [structured workflow audit](workflow-audit.md) for attribution, deduplication and outage behavior. Existing trace commands continue to query trace text; no new investigation command is added in this phase.

## Lockbox API and receipts

`dms machine list` requires **Run open workflows**. The Controller records an automatically approved Lockbox and the CLI waits for the inventory.

```sh
dms machine list
dms machine list --output-format json
dms machine list --output-format csv
dms machine list --show-lockbox-id
```

Open commands accept an optional reason and print the workflow result by default. For the inventory result, JSON is an array, CSV has one row per machine, and an empty list retains the CSV header.

Use `--show-lockbox-id` to print the submission receipt and exit immediately. The receipt contains `LockboxId` and `WorkflowId` in the usual receipt format, including the `Data` envelope for JSON. The Controller automatically approves and runs the workflow as usual. Use `dms workflow status <WorkflowId>` for the result or `dms workflow trace <WorkflowId>` for its trace with the existing workflow read permission.

When waiting times out, the CLI prints the Workflow ID, Lockbox ID and reusable request ID on stderr and exits nonzero. Reuse the same input and request ID to check the original run. The submission API continues to return the receipt described below. SDK callers can use `WaitForOpenResultAsync` with that Lockbox ID to wait for their own result.

An Open workflow that finishes successfully with only logs prints its `WorkflowId` and `State` in the requested format. Workflow authors can use `EmptyResult` to declare this explicitly. Use `dms workflow trace "$WORKFLOW_ID"` with Read DMS operations permission to inspect its archived logs. `Warning` remains a successful CLI exit with a warning on stderr.

`POST /api/v1/dms/lockboxes` returns only these business fields inside the usual AiurProtocol `Data` envelope:

```json
{ "LockboxId": "lb/726c5996-8309-4df9-9b1b-d4eae4a82f2e", "WorkflowId": null }
```

`GET /api/v1/dms/lockboxes?page=0&decision=Pending` lists 50 requests per page, with `Entries`, `Page` and `HasNextPage`. `GET /api/v1/dms/lockboxes/lb/<UUID>` returns the request, frozen input/targets, decision context and optional execution ID. `POST /api/v1/dms/lockboxes/lb/<UUID>/decision` accepts `{ "Approve": true, "Reason": "Reviewed scope" }`. These management endpoints need a personal API Key; reads require Read DMS operations, decisions require the corresponding risk approval permission and self-approval permission when applicable. They create no approval workflow and can be used directly by an independent approval app.

Use the ID printed by the submission to review the frozen workflow definition, version, risk, target machine IDs and input parameters, together with the requester, decision and currently eligible approvers.

```sh
dms lockbox status "$LOCKBOX_ID"
dms lockbox approve "$LOCKBOX_ID" --reason "Reviewed the request"
dms workflow status "$WORKFLOW_ID"
dms workflow callstack "$WORKFLOW_ID"
dms workflow trace "$WORKFLOW_ID"
```

With Client 1.35.0 and an updated Controller, `status` includes the complete review by default: current machine names beside the frozen IDs, typed JSON input values, raw input and stored request JSON, request identifiers and the decision reason. The requester reason is labelled as an unverified statement; compare it against the frozen execution contract before approving. JSON and CSV retain their existing fields and add `Details` (the full Lockbox record) and `MachineNames`. Removed machines remain visible by their frozen IDs. The CLI does not resolve the original machine selector again. Older Controllers still return the available summary.

The requester can check their own Lockbox. Viewing another person's Lockbox or inspecting a Workflow requires operations read permission. The approver list reflects current effective permissions, including self-approval and account lockout. Automatic approvals show the system as the decision maker. The Workflow ID appears when a linked execution exists. `dms lockbox get --id "$LOCKBOX_ID"` continues to return the full request record.

While a request is pending, its requester can run `dms lockbox withdraw --id lb/<UUID>`, use **DMS → My Lockboxes → Withdraw request**, or POST to `/api/v1/dms/lockboxes/lb/<UUID>/withdraw` with their personal API key. Withdrawal takes effect immediately, including after the requester loses the original risk permission. An identical retry returns the withdrawn request. Use `dms lockbox list --decision Withdrawn` with operations read permission to find withdrawn requests.

Approval, denial and withdrawal compete for the same pending request. The first committed decision wins. If approval wins, withdrawal returns HTTP 409 with the workflow ID. To stop that execution, submit `dms operation kill-workflow --id <workflow-id> --reason "Stop requested"`, which follows its own approval policy. Automatically approved submissions already have an execution ID when they return.


Bare historical Lockbox UUIDs remain accepted, but new output consistently uses `lb/`. Workflow IDs remain plain UUIDs. Approved non-execution requests can be represented with a null WorkflowId; this release's submission endpoint still accepts only executable workflow requests. It does not add a mobile app or non-execution request handlers. Update the Controller and CLI together for the new receipt contract; the Agent update includes the nullable enrollment receipt.
