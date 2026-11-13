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

1. Sign in to the Controller as an administrator and change the initial password.
2. Open **Cluster access**, create a cluster, and grant its members the appropriate request, automatic approval and approval permissions for each risk level.
3. Configure self approval separately for users allowed to approve their own requests.
4. Open **API Keys** and create a personal key for CLI access.

Configure the CLI with your own Controller URL and personal key:

```sh
export DMS_SERVER=https://dms.example.com
export DMS_API_KEY='<personal-key>'
```

`--server` and `--api-key` can also be supplied to individual commands. Machine keys are for Agents and cannot be used as personal keys.

## Register a machine

Replace the example IDs with the IDs from your deployment:

```sh
dotnet ./bin/client/dms.dll cluster add-machine \
  --cluster-id <cluster-id> --name server-01 --reason 'Register server'
dotnet ./bin/client/dms.dll lockbox get --id <lockbox-id>
dotnet ./bin/client/dms.dll lockbox approve \
  --id <lockbox-id> --reason 'Registration approved'
```

The submission returns a Lockbox ID. A permitted approver must approve it unless the requester's cluster permission allows automatic approval. After approval, inspect the operation result for the machine ID. Issue that machine's Agent key under **Cluster access**.

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

## Request and inspect operations

```sh
dotnet ./bin/client/dms.dll cluster get-machines \
  --cluster-id <cluster-id> --reason 'Inspect inventory'
dotnet ./bin/client/dms.dll machine performance info \
  --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect capacity'
dotnet ./bin/client/dms.dll machine systemctl restart \
  --cluster-id <cluster-id> --machine-id <machine-id> \
  --service example.service --reason 'Approved maintenance'
dotnet ./bin/client/dms.dll operation get-callstack --id <workflow-id>
dotnet ./bin/client/dms.dll operation get-trace --id <workflow-id>
```

Each operational command creates a Lockbox before execution. The Lockbox ID, its own workflow ID and the approved operation's workflow ID are distinct; use the returned ID for the corresponding inspection command. Inspection and `lockbox approve`/`deny` use authenticated management APIs without creating another Lockbox.

Use `--all-machines` instead of `--machine-id` to freeze the cluster's current machines into a request. A request targets only one cluster. Use `--request-id` when retrying the same submission and `--json` for automation.

`operation kill-workflow --cluster-id <cluster-id> --id <workflow-id> --reason 'Stop operation'` creates a new Lockbox. Cancellation requests cooperative termination; it does not roll back completed side effects. See [recovery and cancellation](./configuration.md#recovery-and-cancellation).
