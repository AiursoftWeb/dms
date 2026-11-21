# Cluster disk usage

Print current capacity for all managed machines or a selected set.

```bash
dms machine disk-usage --all-machines
dms machine disk-usage --machine-id MACHINE_ID_1 MACHINE_ID_2 --output-format json
dms machine disk-usage --all-machines --output-format csv
```

Both this command and `dms machine agent status --all-machines` require **Run open workflows**. A reason is optional. The Controller records and automatically approves a Lockbox for each new request before executing it. The CLI waits for and prints the typed result.

The disk table shows one row per selected machine, highest usage first, followed by totals for the machines that reported capacity. The JSON result contains `TargetMachines`, `MeasuredMachines`, nullable byte totals, and `Machines` with flat per-machine rows. CSV prints those same machine rows with exact byte values and a status column. It has no separate total row. `UsedPercent` is used bytes divided by reported size, rounded to one decimal place. Byte columns are empty for unmeasured machines.

Agent status shows current connectivity, live version, active tasks, and pending events. A connected Agent can still time out or fail its probe. Offline or unresponsive Agents have empty live metric fields. Agent JSON is an array and CSV has one row per selected machine.

## What the numbers mean

Each Agent runs a fixed GNU `df` query with byte units and numeric `size,used,avail` columns. GNU `df` selects local filesystems and handles duplicate bind mounts; mount paths and filesystem names are not parsed from text. See the [GNU Coreutils documentation](https://www.gnu.org/software/coreutils/manual/html_node/df-invocation.html).

The query excludes memory filesystems (`tmpfs`, `devtmpfs`), mounted images (`squashfs`, `iso9660`) and container layers (`overlay`, `aufs`). On a host, those layers are backed by storage already accounted for by its backing filesystem. Default GNU `df` filtering also omits its dummy/inaccessible/duplicate entries; no `--all` flag or explicit path arguments are used. Only mounted filesystems visible in the Agent's mount namespace can be reported. If no eligible filesystems remain, the operation fails instead of reporting a successful zero.

These are **filesystem capacities visible to each machine**, summed across selected machines. They are not physical-disk inventory or a globally deduplicated storage-pool capacity. Shared backing pools, thin provisioning and capacity visible to several machines can overlap; the per-machine results retain the original view. A containerized Agent needs visibility of the intended host filesystems. Its excluded overlay layer is not used as a substitute for host storage.

`AvailableBytes` is the filesystem's reported availability; it is not recomputed as total minus used. Reserved space and other filesystem accounting can make these values differ. Negative availability is preserved, and malformed or unknown byte counts fail. All sums use checked signed 64-bit integers; overflow fails instead of wrapping or using floating-point approximations.

This query does not traverse directories, measure individual file sizes, include unmounted block devices or rescan partitions. Existing `machine layout df` remains a separate fixed query with a broader per-mount display; its unfiltered rows should not be summed to reproduce this aggregate.

## Failure and scheduling

Both workflows probe connected Agents concurrently, with a shared six-second collection window once the Controller workflow starts. They keep one row per frozen target. An offline Agent with no ready session at the start has no child task. An unanswered probe is marked `TimedOut` if the Agent is still connected at the deadline and `Offline` if it disconnected. `Unavailable` indicates a machine that was disabled or removed after the selection was frozen. Child failures and unknown outcomes are recorded as `Failed` and `NeedsVerification`.

An incomplete result is a workflow warning. The CLI emits the result on stdout and a warning with the workflow ID on stderr. Only measured rows contribute to capacity totals. When no measurements are available, totals are null. A probe that timed out before receiving permission to start is cancelled. A running probe receives a cancellation request and keeps its own eventual outcome in the call tree. Late reports do not change the completed overview.

Machine membership is frozen when the Lockbox is submitted. Child probes reuse the root's authorization. Select the original Lockbox and follow its workflow ID to investigate the tree and child traces.

## Implementation and upgrade

The Open Controller roots call the existing typed disk and Agent status Machine workflows. They require no Agent upgrade or database migration. Deploy the Controller before CLI 1.12.0. A pending Safe disk request from the previous definition must be resubmitted as Open. Historical workflow results remain available through the investigation API.
