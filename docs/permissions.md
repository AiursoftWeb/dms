# Workflow permissions

One DMS instance has one global authorization policy. Configure it in **Administration → Directory → Roles → Edit**, using the existing Template permission checkboxes. Permissions from all assigned roles and user claims are combined. Role names do not bypass these checks. OIDC synchronizes identity and role membership at login; permission checkboxes are local to each DMS instance.

| Risk | Request | Automatic approval | Approve / deny |
| --- | --- | --- | --- |
| Open | CanRequestOpenRiskWorkflow | Always with request permission | — |
| Safe | CanRequestSafeRiskWorkflow | CanAutoApproveSafeRiskWorkflow | CanApproveSafeRiskWorkflow |
| Low | CanRequestLowRiskWorkflow | CanAutoApproveLowRiskWorkflow | CanApproveLowRiskWorkflow |
| Medium | CanRequestMediumRiskWorkflow | CanAutoApproveMediumRiskWorkflow | CanApproveMediumRiskWorkflow |
| High | CanRequestHighRiskWorkflow | CanAutoApproveHighRiskWorkflow | CanApproveHighRiskWorkflow |
| VeryHigh | CanRequestVeryHighRiskWorkflow | CanAutoApproveVeryHighRiskWorkflow | CanApproveVeryHighRiskWorkflow |

Request permission is mandatory. For Open, grant the **Run open workflows** permission. Every accepted Open Lockbox is automatically approved by policy. For Safe through VeryHigh, automatic approval additionally needs the matching automatic-approval permission. Approving or denying your own request requires both the relevant risk's approval permission and `CanApproveOwnWorkflow`. Each risk level is granted independently. The risk comes from the compiled public definition, not client input. A submitted request retains its risk, parameters, version and approved machine IDs.

`CanReadDmsOperations` permits machine, Lockbox, workflow, callstack, trace, catalog and investigation queries across the instance. A requester with Open permission can also read their own Open result. `CanManageDmsAccess` manages inventory, join keys and Agent identities; it does not authorize workflow execution. Existing platform permissions continue to control user and role administration. `dms access whoami` requires only your authenticated personal key and reports your own effective matrix, roles and self-approval capability. `--output-format json` returns structured data; CSV returns one row per risk with identity metadata.

Child workflows inherit their root's approval. Calling a higher-risk internal helper does not require another permission or approval. Machine children remain on the same machine, and Controller calls remain confined to the approved machine snapshot. `PublicEntry: false` prevents a definition from being submitted directly, regardless of the caller's permissions. The internal process helper is VeryHigh and is absent from remote dispatch.

## Upgrade and role configuration

This release switches authorization completely to Role/Permission claims. The old scoped grant tables, stored user self-approval flag and machine-group resource are removed. Their configuration APIs and CLI commands no longer exist. Existing histories, identities and keys are retained.

Before enabling operations after a Controller upgrade, use an administrator with the existing role-editing permissions to configure the required checkboxes. No old Cluster grants are copied or unioned into global permissions. Existing roles are not silently modified. A new local installation retains Template's seeded Administrators role; existing OIDC installations continue using their configured roles.

For the company roles, root is intended to have all request and manual approval permissions, self-approval, read and management access. Leadership is intended to approve all five manually reviewable risks without self approval. Employees receive only the explicitly chosen request/automatic permissions. Configure each risk independently; request and automatic approval cells are not inferred from a job title. In particular, do not enable automatic VeryHigh approval as a side effect of this upgrade. Configure the actual production matrix before rollout.

Personal API keys resolve the user's current Identity permissions on each request, so changing roles does not require a replacement key. Delegated machine enrollment uses the join-key issuer's current request permissions. Web sessions retain the normal Template identity/session lifecycle; operational decisions and the effective matrix are resolved from current Identity claims.

Update the Controller and CLI together (`Aiursoft.DMS.Client` 1.1.0). Agent task/response payloads do not change; existing Agent installations can stay connected and upgrade through DMS. Agent 1.1.0 supplies the scope-free join CLI. See the [inventory upgrade procedure](configuration.md#upgrade-to-instance-wide-inventory) before migrating existing data. Operations still return Lockbox/workflow IDs and use the same `lockbox get`, `operation get-workflow`, `operation get-callstack` and `operation get-trace` commands.

## Open inventory and machine overviews

Deploy the new Controller before using CLI 1.12.0. In the role editor, grant **Run open workflows** to roles that should be able to list the inventory or run live disk and Agent health overviews across the instance. Existing Safe grants do not grant Open access. The Controller records the Open decision in the normal Lockbox audit before executing the requested workflow. See [examples and output fields](disk-usage.md).

## Compiled machine schedules

`[RunCron]` declares a built-in system schedule authorized by its reviewed release. It runs locally after Agent deployment, with no human requester or per-run Lockbox. Risk remains a declared capability label; it does not grant human permissions. There is no user-facing dynamic schedule creation API in this release. An automatically approved human request still has `Manual` origin.

The shipped scheduled Agent-status inspection is Safe, performs a read-only typed child call, and has no direct public request entry. Reading its IDs/tree/trace requires `CanReadDmsOperations`, just like other history. Cancelling one invocation uses the existing Medium-risk `operation kill-workflow` request: the requester needs `CanRequestMediumRiskWorkflow`; automatic approval needs `CanAutoApproveMediumRiskWorkflow`, otherwise a reviewer needs `CanApproveMediumRiskWorkflow`, plus `CanApproveOwnWorkflow` when approving their own request. Cancellation does not remove the compiled plan, so later occurrences remain enabled.

## Personal SSH public keys

Open **Settings → Personal → SSH public keys** to register an OpenSSH public key. Run the displayed signing command on the computer that holds the private key and paste the signature back within ten minutes. Each employee can hold three verified keys and one pending challenge. Starting a new registration replaces their previous challenge. Revocation releases a slot and preserves the registration ID and fingerprint for audit. Registering a key grants no server access.
