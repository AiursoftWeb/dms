# Discover standard workflows

[CLI setup](cli-and-agent.md) · [Authoring workflows](authoring-workflows.md)

```sh
dms operation list-definitions --cluster-id <cluster-id>
dms operation list-definitions --cluster-id <cluster-id> --output-format json
dms operation list-definitions --cluster-id <cluster-id> --output-format csv
```

The authenticated query reads the Controller's compiled registration catalog. It is audited and creates no Lockbox or workflow instance. It requires view access to the selected cluster (at least one request or approval permission). Machine keys cannot query it.

Every registered public standard definition appears, including definitions you cannot request. Rows show the exact name and version, Cluster or Machine location, risk, whether machine targets are required, and the three independent permissions for that risk in the selected cluster. An absent risk grant means all three permissions are false. Automatic approval only takes effect when you may request; it does not grant request permission by itself. `CanApprove` covers that risk level; approving your own request additionally requires the separate user-level `CanApproveOwnRequests` setting.

Table output shows metadata and readable risk/location names. JSON preserves the response envelope with `Data.ClusterId`, `Data.CanApproveOwnRequests` and `Data.Definitions`; enums follow the existing numeric wire convention. CSV has one row per definition, repeating ClusterId and CanApproveOwnRequests alongside the definition fields. CSV has no rows when no public definitions are registered.

Open **Standard workflows** in the web UI to select a visible cluster and inspect the same catalog and permissions. Workflow names link to that definition's history in the selected cluster.

Internal helpers are excluded, even if you can request every risk level. A definition without a registered execution factory is also excluded. Special control operations such as machine registration and workflow cancellation are separate existing commands and are not standard definitions in this catalog. This list does not guarantee an Agent is online or has the required version. Actual submission and approval recheck permissions and definitions; the catalog is a snapshot, not authorization to execute.

The SDK provides `DmsApiClient.ListDefinitionsAsync(clusterId)`. Its HTTP endpoint is `GET /api/v1/dms/workflow-definitions?clusterId=<id>`, using the same personal API Key and AiurProtocol envelope as other management queries. Input schemas and generated CLI commands are not provided by this endpoint.
