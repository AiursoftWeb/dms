# Discover standard workflows

[All Workflows](all-workflows.md) · [CLI setup](cli-and-agent.md) · [Authoring workflows](authoring-workflows.md)

```sh
dms operation list-definitions
dms operation list-definitions --output-format json
dms operation list-definitions --output-format csv
```

The authenticated query reads the Controller's compiled registration catalog. It is audited and creates no Lockbox or workflow instance. It requires the global `CanReadDmsOperations` permission. Machine keys cannot query it.

Every registered public standard definition appears, including definitions you cannot request. Rows show the exact name and version, Cluster or Machine location, risk, whether machine targets are required, and the three independent permissions for that risk across the DMS instance. An absent Permission means all three permissions are false. Automatic approval only takes effect when you may request; it does not grant request permission by itself. `CanApprove` covers that risk level; approving your own request additionally requires the `CanApproveOwnWorkflow` Permission; the returned `CanApproveOwnRequests` is computed from that Permission.

Table output shows metadata and readable risk/location names. JSON preserves the response envelope with `Data.CanApproveOwnRequests` and `Data.Definitions`; enums follow the existing numeric wire convention. CSV has one row per definition, repeating CanApproveOwnRequests alongside the definition fields. CSV has no rows when no public definitions are registered.

Open **Standard workflows** in the web UI to inspect the same catalog and permissions across the instance. Workflow names link to that definition's history across all machines.

The catalog includes public Controller workflows such as adding a machine and requesting workflow cancellation. See [All Workflows](all-workflows.md) for scheduled entries, internal helpers, and Controller dispatch. Actual submission and approval recheck permissions and definitions. Execution on a machine requires a reachable Agent that supports the selected definition version.

The SDK provides `DmsApiClient.ListDefinitionsAsync()`. Its HTTP endpoint is `GET /api/v1/dms/workflow-definitions`, using the same personal API Key and AiurProtocol envelope as other management queries. Input schemas and generated CLI commands are not provided by this endpoint.
