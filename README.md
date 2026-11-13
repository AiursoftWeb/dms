# DMS

Approved server operations for humans and AI. A Controller manages inventory, Lockbox approvals and workflow history. Linux Agents connect outward over WebSocket; managed machines need no SSH service or internet access.

The initial workflows are performance information, service restart, machine inventory and registration. Arbitrary commands are not exposed. Every operational CLI command submits a Lockbox; inspection and approval commands use authenticated management APIs.

## Run

Build with the .NET 10 SDK:

```sh
dotnet build Aiursoft.DMS.sln
dotnet run --project src/Aiursoft.DMS/Aiursoft.DMS.csproj
```

Use the Template local administrator for initial setup, change its password, then open **Cluster access** to create a cluster and assign each risk level's request, automatic approval and approval permissions. Self approval is a separate user setting. Create a personal key under **API Keys**.

```sh
export DMS_SERVER=https://dms.example.com
export DMS_API_KEY='<personal-key>'
dotnet run --project src/Aiursoft.DMS.Client -- cluster add-machine --cluster-id <cluster-id> --name server-01 --reason 'Register server'
```

After approval, inspect the operation result for the machine ID. Issue its Agent key under **Cluster access**. Install the published Agent and .NET 10 runtime using your internal distribution channel. Run it with a dedicated persistent private state directory:

```sh
export DMS_CONTROLLER=https://dms.example.com
export DMS_MACHINE_KEY='<agent-key>'
export DMS_STATE_PATH=/var/lib/dms-agent
dotnet Aiursoft.DMS.Agent.dll
```

The machine is bound to its state directory's installation identity. Preserve that directory across upgrades. A second installation with the same key is rejected until pending operations have been reconciled.

```sh
dotnet dms.dll machine performance info --cluster-id <cluster-id> --machine-id <machine-id> --reason 'Inspect capacity'
dotnet dms.dll machine systemctl restart --cluster-id <cluster-id> --machine-id <machine-id> --service example.service --reason 'Approved maintenance'
dotnet dms.dll lockbox get --id <lockbox-id>
dotnet dms.dll lockbox approve --id <lockbox-id> --reason 'Maintenance window confirmed'
dotnet dms.dll operation get-callstack --id <workflow-id>
dotnet dms.dll operation get-trace --id <workflow-id>
```

Use `--all-machines` to freeze the cluster's current machines into a request, `--request-id` when retrying the same submission, and `--json` for automation. The five risk levels have independent permissions in each cluster. A request can target only one cluster.

## Persistence

Run one Controller with persistent database and `Dms:StatePath` storage. Configure `Dms:Clickhouse:Enabled` and `Dms:Clickhouse:ConnectionString` for workflow traces; application logging uses the separate Template logging settings. Detailed traces are spooled on disk and archived into `DmsWorkflowTrace`, not the relational business database. Unavailable storage applies backpressure instead of dropping entries.

Unstarted tasks resume dispatch. Started tasks with unknown outcomes become `NeedsVerification` and are never automatically rerun. `kill-workflow` requires a new Lockbox; cancellation does not undo side effects or prove that a systemd job was rolled back.

The Web UI provides machines, approval context, workflow history, callstacks and trace. Query access follows cluster membership. Default development authentication is Local; configure OIDC through the existing Template settings when deploying.
