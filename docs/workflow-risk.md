# Workflow risk classification

A public workflow's risk level determines who may request it, whose requests may run automatically, and who may approve it. The Controller applies the level declared in the shared workflow definition to every new Lockbox. Authors and reviewers must classify the capability granted by **one approval**, across all accepted inputs, targets and child calls.

## The six levels

| Level | Assign it when | Example |
| --- | --- | --- |
| Open (放开) | A read-only investigation has negligible cost and returns bounded, low-sensitivity fields suitable for immediate display to everyone with Open request permission. Each accepted request receives automatic approval. | List managed machines and display their inventory summaries. |
| Safe (安全) | A read-only investigation has negligible cost and low-sensitivity output. The ordinary Lockbox receipt and workflow report are appropriate, and the role controls automatic approval. | Read filesystem capacity with `machine.layout.df`. |
| Low (低危) | A read-only investigation consumes appreciable resources or reveals internal information. Bound its scope, duration and output according to the operation. | Scan directory usage, sample network counters, read a service log, or query rows through a verified read-only database connection. |
| Medium (中危) | The caller selects a predefined change with a bounded effect on the approved targets. The operation can interrupt service or cause permanent loss. | Stop a specified service, power off a machine, or permanently remove one literal path with the approved recursion setting. |
| High (高危) | The caller supplies executable content or chooses code to run. DMS can identify the approved program, command, image or writable SQL and associate the execution and its outcome with that request. | Run a supplied script, execute a command in a container, change a service to a caller-selected image, or execute caller-supplied writable SQL. |
| VeryHigh (超高危) | The operation grants access that can be used independently of the workflow. DMS can audit the grant but cannot reconstruct the subsequent actions taken through it. | Open a temporary SSH lease. DMS can record the requester, reviewer, machine and lease lifecycle. Commands entered during the session are outside that workflow's audit record. |

## Applying the rule

1. Describe what the requester controls. Include target selection, accepted arguments, artifacts, privileges and output. Evaluate the most powerful permitted input and the full approved target set. A Cluster workflow can have the same risk as a Machine workflow.
2. Trace the capability through its children. A parent authorizes all its child calls. Restarting a validated service through an internal process helper is Medium. Accepting an executable and caller-supplied arguments is High. A parent that exposes an SSH session grants VeryHigh access even when a child implements the lease.
3. Distinguish sensitive observations from reusable access. A bounded query that reads business data can be Low. Returning a private credential can let its holder act later, outside DMS. Review the access that credential confers. For file transfers, consider both ends and what the recipient can subsequently execute or access.
4. For a fixed destructive action, show the target and the parameters that determine the damage in the approval record. For example, `filesystem.rm` freezes a literal path and the recursive flag. A directory's contents can change between request and execution. If a deletion stops partway through, inspect the target before submitting another request.
5. For Open, review the exact fields the requester will see and implement the direct CLI result. Open requires an authenticated requester with the Open permission. The Controller records the Lockbox, automatic decision and workflow ID. `--show-lockbox-id` requests the receipt when the caller needs the IDs.

## Reviewing a classification change

Declare the risk once on the shared definition with `[WorkflowRisk]`, for example `[WorkflowRisk(RiskLevel.Medium)]`. Review both the data returned to the caller and the details exposed through reports, artifacts and trace. In the change description, identify the old and new levels, representative accepted inputs, the worst permitted effect, and which role permissions now apply. Include an authorization test through the normal request path and update the published workflow catalog.

Each level has its own grants. A change can expand access for roles holding the new level and remove access for roles holding only the old one. Manual self-approval also requires the self-approval permission. Explicit independent-review rules apply where configured in the workflow submission path. A built-in Cron schedule uses its release-authorized system identity.

Existing Lockboxes retain the risk and targets recorded at submission. When a pending request's recorded risk differs from the current definition, the requester must submit a new request before execution. Completed decisions and workflow history keep their original audit records. Operators can inspect their effective grants with `dms access whoami` and the Controller's current definitions with `dms operation list-definitions`.

See [Role permissions](permissions.md), [Authoring workflows](authoring-workflows.md) and [All workflows](all-workflows.md).
