# Cluster disk usage

Submit a Safe, read-only operation for a frozen set of machines:

```bash
dms machine disk-usage --all-machines --reason "Review all machine capacity"
# Or select specific machines:
dms machine disk-usage --machine-id MACHINE_ID_1 MACHINE_ID_2 --reason "Review selected capacity"
```

The command returns a Lockbox request. The global Safe permissions determine whether it is automatically approved or needs another approver. After approval, use the operation ID returned by `lockbox get`:

```bash
dms workflow status OPERATION_ID
dms workflow callstack OPERATION_ID
dms workflow trace OPERATION_ID
```

The result contains `TotalBytes`, `UsedBytes`, `AvailableBytes`, `MachineCount` and a per-machine `Machines` list. Each entry contains its `MachineId` and `Usage`, including the Machine child's full `WorkflowId`, byte counts and filesystem count. That ID opens the child through the same operation commands. Table, JSON and CSV output use the common `--output-format` option.

## What the numbers mean

Each Agent runs a fixed GNU `df` query with byte units and numeric `size,used,avail` columns. GNU `df` selects local filesystems and handles duplicate bind mounts; mount paths and filesystem names are not parsed from text. See the [GNU Coreutils documentation](https://www.gnu.org/software/coreutils/manual/html_node/df-invocation.html).

The query excludes memory filesystems (`tmpfs`, `devtmpfs`), mounted images (`squashfs`, `iso9660`) and container layers (`overlay`, `aufs`). On a host, those layers are backed by storage already accounted for by its backing filesystem. Default GNU `df` filtering also omits its dummy/inaccessible/duplicate entries; no `--all` flag or explicit path arguments are used. Only mounted filesystems visible in the Agent's mount namespace can be reported. If no eligible filesystems remain, the operation fails instead of reporting a successful zero.

These are **filesystem capacities visible to each machine**, summed across selected machines. They are not physical-disk inventory or a globally deduplicated storage-pool capacity. Shared backing pools, thin provisioning and capacity visible to several machines can overlap; the per-machine results retain the original view. A containerized Agent needs visibility of the intended host filesystems. Its excluded overlay layer is not used as a substitute for host storage.

`AvailableBytes` is the filesystem's reported availability; it is not recomputed as total minus used. Reserved space and other filesystem accounting can make these values differ. Negative availability is preserved, and malformed or unknown byte counts fail. All sums use checked signed 64-bit integers; overflow fails instead of wrapping or using floating-point approximations.

This query does not traverse directories, measure individual file sizes, include unmounted block devices or rescan partitions. Existing `machine layout df` remains a separate fixed query with a broader per-mount display; its unfiltered rows should not be summed to reproduce this aggregate.

## Failure and scheduling

The first version requires a complete successful result from every selected machine. It does not publish partial totals. A known child failure fails the parent; a started child with an unknown outcome makes the parent need verification. Completed and failed children stay in the call tree for investigation. An offline machine can leave its child queued and its parent waiting. Use the existing approved kill operation if cancellation is needed.

The existing bounded process-output and workflow-result limits still apply. An oversized query or aggregate fails rather than truncating the result or publishing partial totals.

Machine membership is frozen when the Lockbox is submitted. Adding a machine while approval is pending does not expand execution. No additional approvals are created for the Machine query or its internal process child.

## A standard workflow authoring example

The implementation uses the same extension points described in [Adding a standard workflow](authoring-workflows.md):

- Shared typed definitions and results in Contracts.
- `MachineDiskUsageWorkflow` for filesystem selection, numeric parsing and local aggregation. It is internal and has no direct CLI or public Lockbox entry.
- `ClusterDiskUsageWorkflow` for a sequential `foreach`, typed `await` and checked sums.
- One execution-factory registration per workflow, plus the CLI option mapping.

No scheduler algorithm, approval service, database schema, message model or output renderer was extended for this operation. The registry constructor is the only changed part of the existing runtime file. Integration tests exercise the product registrations and actual Agents; they do not replace these implementations with the earlier authoring examples.
