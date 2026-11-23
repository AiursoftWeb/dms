# All Workflows

[Discover workflows and permissions](workflow-discovery.md) · [CLI and Agent setup](cli-and-agent.md) · [Operation options](operations.md)

Each row shows the workflow ID you will see in execution history, how it starts, its schedule in UTC, and what it does. DMS has 123 requestable entries in this catalog and 9 scheduled or internal entries, including the Controller’s dispatch workflow.

Enter a CLI command with `--help` to see its options. Workflow requests generally need a reason and, where applicable, a machine target. The `Open` inventory workflow uses the **Run open workflows** permission. For other risk levels, request, automatic approval, and manual approval are separate role permissions. Self-approval requires its own permission. Internal and scheduled entries show their registered risk and run through their parent or schedule. See [Role permissions](permissions.md).

Run `dms operation list-definitions` to check the public definitions and your permissions on the Controller you use. Installed Agents must support the applicable workflow version. Review **Workflow history** for actual scheduled executions. A dash in the schedule column means the workflow has no independent schedule.

| Workflow | ID | How to trigger | Schedule (UTC) | Behavior |
| --- | --- | --- | --- | --- |
| Add a machine | `cluster.add-machine` | `dms machine add` (Medium) | — | Adds an inventory record. An Agent enrollment can submit the same workflow and associates the approved machine with that enrollment. |
| Summarize fleet disk usage | `cluster.disk-usage` | `dms machine disk-usage` (Safe) | — | Calls the disk-usage child on each selected machine and aggregates filesystem capacity, usage, and partial results. |
| List managed machines | `cluster.get-machines` | `dms machine list` (Open) | — | Returns the Controller inventory with machine names, IDs, Agent versions, and last-seen timestamps. Run open workflows permission automatically approves the Lockbox request. |
| Dispatch approved machine work | `cluster.machine-batch` | Controller creates the root after a Machine workflow request is approved | — | Creates one child run per approved target. The Lockbox binds the requested definition, version, parameters, and targets. |
| Cancel a workflow | `operation.kill-workflow` | `dms operation kill-workflow` (Medium) | — | Requests cancellation of an existing workflow and its descendants. Effects already applied stay in place. |
| Scheduled Agent health check | `machine.agent.scheduled-status` | Agent schedule and startup (Safe) | Every 5 minutes and at Agent startup | Calls the Agent status workflow and records the Agent identity, version, and task and event backlog. |
| Scheduled Agent upgrade | `machine.agent.scheduled-upgrade` | Agent schedule on every managed machine (High) | 00:00, 08:00, and 16:00 daily | Checks for a newer Controller release. Waits for other workflows to exit and for 10 seconds of continuous idle time before upgrading. |
| Inspect Agent status | `machine.agent.status` | `dms machine agent status` (Safe) | — | Check Agent version, connection identity, and pending events. |
| Upgrade an Agent | `machine.agent.upgrade` | `dms machine agent upgrade` (High) | — | Installs the Controller-selected Agent release. The workflow checks that the upgraded Agent reconnects. |
| Inspect APT packages | `machine.apt.inspect` | `dms machine apt inspect` (Low) | — | Show installed packages and available APT updates. |
| Install APT packages | `machine.apt.install` | `dms machine apt install` (High) | — | Install the requested APT packages. |
| Upgrade APT packages | `machine.apt.upgrade` | `dms machine apt upgrade` (High) | — | Upgrade the specified installed APT packages. |
| Refresh APT indexes | `machine.apt.update` | `dms machine apt update` (Medium) | — | Refresh each target's package indexes. Any source error fails that target. |
| Preview APT maintenance | `machine.apt.plan` | `dms machine apt upgrade-all --dry-run` or `dms machine apt autoremove --dry-run` (Low) | — | Record each target's package actions using its existing indexes. |
| Upgrade available APT packages | `machine.apt.upgrade-all` | `dms machine apt upgrade-all --plan` (High) | — | Upgrade the reviewed per-machine packages and any approved new dependencies without removals. |
| Remove unused APT packages | `machine.apt.autoremove` | `dms machine apt autoremove --plan` (High) | — | Remove the reviewed unused packages. `--purge` also removes their package configuration files. |
| Configure Box Git access | `machine.box.configure-git` | `dms machine box configure-git` (High) | — | Configures which existing SSH identity file and known-hosts file Box uses for Git pulls. |
| Deploy a Box | `machine.box.deploy` | `dms machine box deploy` (High) | — | Updates the Box checkout and deploys from the selected stage onward. The service filter can limit the deployment. |
| Inspect a Box checkout | `machine.box.inspect` | `dms machine box inspect` (Safe) | — | Produces a structured report of Box Git status, stages, stacks, Swarm running stacks, service replica health, and secrets inventory. |
| Refresh images with Box Puller | `machine.box.puller.force-update` | `dms machine box puller force-update` (High) | — | Refreshes a selected image or the configured image set through the specified Box Puller. |
| Find a ClickHouse container | `machine.clickhouse.find-container` | `dms machine clickhouse find-container` (Open) | — | Find the unique running ClickHouse server container on the target machine. |
| List ClickHouse databases | `machine.clickhouse.list-databases` | `dms machine clickhouse list-databases` (Open) | — | List databases visible to the local read account. |
| List ClickHouse tables | `machine.clickhouse.list-tables` | `dms machine clickhouse list-tables` (Open) | — | List visible tables in one database. |
| Describe a ClickHouse table | `machine.clickhouse.describe-table` | `dms machine clickhouse describe-table` (Open) | — | Display column names and types. |
| Investigate ClickHouse | `machine.clickhouse.query` | `dms machine clickhouse sql` (Low) | — | Run one bounded read-only statement and return its rows to the requester. |
| Change ClickHouse data or columns | `machine.clickhouse.execute` | `dms machine clickhouse sql` (High) | — | Apply an approved write to the frozen local container. |
| Describe a database table | `machine.database.describe-table` | `dms machine database describe-table` (Low) | — | Show a database table's columns and types. |
| Inspect database activity | `machine.database.diagnostics` | `dms machine database diagnostics` (Medium) | — | Inspect a database's active queries, connections, and resource usage. |
| Discover database services | `machine.database.discover` | `dms machine database discover` (Low) | — | Find local database containers and configured connection names. |
| Execute SQL from a file | `machine.database.execute-sql` | `dms machine database execute-sql` (VeryHigh) | — | Runs approved SQL from a file against a configured writable connection. The file can contain writes or schema changes. |
| List databases | `machine.database.list-databases` | `dms machine database list-databases` (Low) | — | List databases available through a configured connection. |
| List tables | `machine.database.list-tables` | `dms machine database list-tables` (Low) | — | List tables in the specified database. |
| Query SQL from a file | `machine.database.query-sql` | `dms machine database query-sql` (High) | — | Runs SQL from a local file through a configured read-only database connection with bounded output. |
| Query table rows | `machine.database.query-table` | `dms machine database query-table` (Medium) | — | Read selected table columns with optional filters and sorting. |
| Diagnose Docker startup | `machine.docker.diagnose-startup` | `dms machine docker diagnose-startup` (Medium) | — | Gather a container's state, recent logs, and startup events. |
| Read Docker events | `machine.docker.events` | `dms machine docker events` (Low) | — | Read recent Docker events for a container. |
| Run a command in Docker | `machine.docker.exec` | `dms machine docker exec` (VeryHigh) | — | Runs an approved executable and arguments inside the selected Docker container. |
| Copy files out of a container | `machine.docker.filesystem.cp-from` | `dms machine docker filesystem cp-from` (VeryHigh) | — | Copy a file or directory from a container to its host. |
| Copy files into a container | `machine.docker.filesystem.cp-to` | `dms machine docker filesystem cp-to` (VeryHigh) | — | Copy a file or directory from the host into a container. |
| Export container files | `machine.docker.filesystem.export` | `dms machine docker filesystem export` (High) | — | Export a container file or directory as a downloadable artifact. |
| Inspect a Docker container | `machine.docker.inspect` | `dms machine docker inspect` (Low) | — | Show container image, state, health, and mounts. |
| Kill a Docker container | `machine.docker.kill` | `dms machine docker kill` (High) | — | Send SIGKILL to a running Docker container. |
| Read Docker logs | `machine.docker.logs` | `dms machine docker logs` (Low) | — | Read recent stdout and stderr from a Docker container. |
| List Docker containers | `machine.docker.ps` | `dms machine docker ps` (Low) | — | List Docker containers and their full IDs. |
| Restart a Docker container | `machine.docker.restart` | `dms machine docker restart` (High) | — | Restart a standalone container. |
| Start a Docker container | `machine.docker.start` | `dms machine docker start` (High) | — | Start an existing standalone container. |
| Sample Docker resource usage | `machine.docker.stats-sample` | `dms machine docker stats-sample` (Low) | — | Sample Docker container CPU, memory, and I/O usage. |
| Stop a Docker container | `machine.docker.stop` | `dms machine docker stop` (High) | — | Gracefully stop a standalone container. |
| Inspect Docker storage usage | `machine.docker.system.df` | `dms machine docker system df` (Low) | — | Show disk space used by Docker images, containers, volumes, and build cache. |
| Read the start of a file | `machine.filesystem.cat` | `dms machine filesystem cat` (High) | — | Read the first lines of a text file. |
| Copy files | `machine.filesystem.cp` | `dms machine filesystem cp` (VeryHigh) | — | Copy a file or directory on the same machine. |
| Collect machine disk usage | `machine.filesystem.disk-usage` | Fleet disk-usage workflow (Safe) | — | Collects filesystem capacity and usage for one machine and returns its observations to the fleet disk-usage workflow. |
| Measure directory disk usage | `machine.filesystem.du` | `dms machine filesystem du` (Low) | — | Measure disk usage under a directory. |
| Export machine files | `machine.filesystem.export` | `dms machine filesystem export` (High) | — | Packages an approved path as an authorized downloadable artifact. |
| Find files | `machine.filesystem.find` | `dms machine filesystem find` (Low) | — | Find files by name, size, or modification time. |
| Import a file artifact | `machine.filesystem.import` | `dms machine filesystem import` (VeryHigh) | — | Writes the approved artifact to the destination path. Overwrite must be explicitly requested. |
| List directory entries | `machine.filesystem.ls` | `dms machine filesystem ls` (Low) | — | List files and directories under a path. |
| Create a directory | `machine.filesystem.mkdir` | `dms machine filesystem mkdir` (High) | — | Create a directory on a machine. |
| Move or rename files | `machine.filesystem.mv` | `dms machine filesystem mv` (VeryHigh) | — | Move or rename a file or directory on the same machine. |
| Remove a file or directory | `machine.filesystem.rm` | `dms machine filesystem rm` (High) | — | Permanently removes the approved path. Directory recursion requires an explicit request. |
| Inspect file metadata | `machine.filesystem.stat` | `dms machine filesystem stat` (Low) | — | Read a file's type, size, ownership, permissions, and timestamps. |
| Read the end of a file | `machine.filesystem.tail` | `dms machine filesystem tail` (High) | — | Read the last lines of a text file. |
| Audit bcache settings | `machine.layout.bcache-audit` | `dms machine layout bcache-audit` (Low) | — | Compare the machine's bcache configuration with a baseline. |
| Inspect bcache | `machine.layout.bcache-info` | `dms machine layout bcache-info` (Low) | — | Show bcache devices, modes, and counters. |
| Inspect filesystem capacity | `machine.layout.df` | `dms machine layout df` (Safe) | — | Show used and available space on mounted filesystems. |
| Inspect disk partition tables | `machine.layout.fdisk-query` | `dms machine layout fdisk-query` (Safe) | — | Read partition tables and disk geometry. |
| Inspect inode usage | `machine.layout.inodes` | `dms machine layout inodes` (Safe) | — | Show used and available inodes on mounted filesystems. |
| List block devices | `machine.layout.lsblk` | `dms machine layout lsblk` (Safe) | — | List disks, partitions, and their filesystems. |
| List mounted filesystems | `machine.layout.mounts` | `dms machine layout mounts` (Low) | — | Show mounted filesystems, sources, and mount options. |
| Find open deleted files | `machine.layout.open-deleted` | `dms machine layout open-deleted` (Low) | — | Find deleted files that processes still hold open. |
| Read SMART data | `machine.layout.smart` | `dms machine layout smart` (Low) | — | Read a disk's SMART health and error information. |
| Run a short SMART self-test | `machine.layout.smart-test` | `dms machine layout smart-test` (Medium) | — | Starts a short SMART self-test and polls for completion within the requested wait budget. |
| Resolve a DNS name | `machine.networking.dns-query` | `dms machine networking dns-query` (Low) | — | Resolve a hostname from the selected machine. |
| Inspect firewall rules | `machine.networking.firewall-info` | `dms machine networking firewall-info` (Low) | — | Read firewall rules and packet counters. |
| Inspect network interfaces | `machine.networking.interfaces` | `dms machine networking interfaces` (Low) | — | Show network interfaces, addresses, and link counters. |
| Probe network connectivity | `machine.networking.probe` | `dms machine networking probe` (Low) | — | Tests TCP reachability from the selected machine and optionally reads the HTTP response status. |
| Inspect routes | `machine.networking.routes` | `dms machine networking routes` (Low) | — | Show routing tables and policy routing rules. |
| Inspect network sockets | `machine.networking.sockets` | `dms machine networking sockets` (Low) | — | Show listening sockets, connections, and their processes. |
| Check endpoint TLS | `machine.networking.tls-check` | `dms machine networking tls-check` (Low) | — | Check an endpoint's TLS handshake and certificate. |
| Sample network traffic | `machine.networking.traffic-sample` | `dms machine networking traffic-sample` (Medium) | — | Samples interface counters and connection metadata. |
| Inspect NVIDIA drivers | `machine.nvidia.driver-info` | `dms machine nvidia driver-info` (Low) | — | Check NVIDIA driver, CUDA, and container runtime information. |
| Read NVIDIA errors | `machine.nvidia.errors` | `dms machine nvidia errors` (Low) | — | Read NVIDIA driver and GPU error events. |
| Inspect NVIDIA GPU overview | `machine.nvidia.overview` | `dms machine nvidia overview` (Low) | — | Show NVIDIA GPUs, driver, memory, active processes, and topology in one trace. |
| List NVIDIA GPU processes | `machine.nvidia.processes` | `dms machine nvidia processes` (Low) | — | List processes using NVIDIA GPUs. |
| Sample NVIDIA GPU usage | `machine.nvidia.sample` | `dms machine nvidia sample` (Low) | — | Sample NVIDIA GPU utilization and memory over time. |
| Inspect NVIDIA GPU status | `machine.nvidia.smi` | `dms machine nvidia smi` (Safe) | — | Show NVIDIA GPU usage, memory, temperature, and power. |
| Inspect NVIDIA GPU topology | `machine.nvidia.topology` | `dms machine nvidia topology` (Low) | — | Show NVIDIA GPU interconnects and topology. |
| Find blocked processes | `machine.performance.blocked-processes` | `dms machine performance blocked-processes` (Medium) | — | Find processes blocked in uninterruptible sleep. |
| Sample CPU activity | `machine.performance.cpu-sample` | `dms machine performance cpu-sample` (Low) | — | Sample CPU utilization, I/O wait, and steal time by core. |
| Sample system activity | `machine.performance.dstat-sample` | `dms machine performance dstat-sample` (Low) | — | Sample CPU, memory, disk, and network activity together. |
| Discover performance archives | `machine.performance.history-sources` | `dms machine performance history-sources` (Low) | — | Find historical performance data and archive IDs. |
| Read CPU history | `machine.performance.history.cpu` | `dms machine performance history cpu` (Low) | — | Read recorded CPU utilization. |
| Read disk I/O history | `machine.performance.history.disk` | `dms machine performance history disk` (Low) | — | Read recorded block-device I/O. |
| Read load history | `machine.performance.history.load` | `dms machine performance history load` (Low) | — | Read recorded system load averages. |
| Read memory and swap history | `machine.performance.history.memory` | `dms machine performance history memory` (Low) | — | Read recorded memory and swap usage. |
| Read network history | `machine.performance.history.network` | `dms machine performance history network` (Low) | — | Read recorded network interface activity. |
| Inspect machine performance | `machine.performance.info` | `dms machine performance info` (Safe) | — | Show a machine's CPU, memory, and load snapshot. |
| Sample block-device I/O | `machine.performance.io-sample` | `dms machine performance io-sample` (Low) | — | Sample block-device I/O over time. |
| Inspect process memory | `machine.performance.memory-breakdown` | `dms machine performance memory-breakdown` (Low) | — | Break down RAM, swap, and memory usage by process. |
| Read OOM history | `machine.performance.oom-history` | `dms machine performance oom-history` (Medium) | — | Find recent out-of-memory and OOM-kill events. |
| Read Performance Co-Pilot history | `machine.performance.pcp-history` | `dms machine performance pcp-history` (Medium) | — | Read selected metrics from a Performance Co-Pilot archive. |
| Read resource pressure | `machine.performance.pressure` | `dms machine performance pressure` (Safe) | — | Read CPU, memory, and I/O stall metrics. |
| List resource usage by process | `machine.performance.processes` | `dms machine performance processes` (Low) | — | List processes sorted by CPU, memory, or process ID. |
| Audit local accounts | `machine.security.accounts-audit` | `dms machine security accounts-audit` (Medium) | — | Reads login accounts, privileged groups, sudo rules, and authorized-key file metadata. |
| Apply the security baseline | `machine.security.apply-baseline` | Agent schedule. Application requires a matching Controller policy. (High) | Daily at 03:00 UTC | Fetches the target machine’s policy from the Controller. On included machines, it closes ordinary SSH when no lease remains and preserves valid leases. The policy can also install Fail2ban and set UFW. UFW changes wait while a lease is present. |
| Read authentication events | `machine.security.auth-events` | `dms machine security auth-events` (Medium) | — | Read recent login, authentication, and privilege events. |
| Audit the security baseline | `machine.security.baseline-audit` | `dms machine security baseline-audit` (Medium) | — | Collects SSH, Secure Boot, encryption, Fail2ban, Clean Traffic, and firewall evidence. It compares observations with a versioned baseline when supplied. |
| Inspect Clean Traffic | `machine.security.clean-traffic-status` | `dms machine security clean-traffic-status` (Low) | — | Check Clean Traffic service status and recent activity. |
| Inspect disk encryption | `machine.security.disk-encryption` | `dms machine security disk-encryption` (Low) | — | Show encryption status for disks and mounted filesystems. |
| Inspect Fail2ban | `machine.security.fail2ban-status` | `dms machine security fail2ban-status` (Low) | — | Check Fail2ban status, jails, and active bans. |
| Check Secure Boot | `machine.security.secure-boot` | `dms machine security secure-boot` (Safe) | — | Check whether UEFI Secure Boot is enabled. |
| Audit SSH configuration | `machine.security.ssh-audit` | `dms machine security ssh-audit` (Low) | — | Reads effective OpenSSH settings for a specified user, client address, and host in a local configuration check. |
| Disable SSH | `machine.ssh.disable` | `dms machine ssh disable` (Medium) | — | Masks and stops the system SSH service and socket. An existing DMS lease is revoked immediately, with its sessions stopped and its temporary account and access removed. |
| Reconcile SSH leases | `machine.ssh.reconcile-lease` | Agent schedule and startup (Medium) | Every 5 minutes and at Agent startup | Reclaims expired or invalid SSH leases on the Agent. Valid leases remain active. Without a lease, the existing SSH state is preserved. |
| Open a temporary SSH lease | `machine.ssh.temp-allow-ssh` | `dms machine ssh temp-allow-ssh` (VeryHigh) | — | Opens a temporary account with a verified personal key after managed closure is established. One lease per machine, 30 minutes by default, 30–270 minutes supported. Sudo is opt-in. |
| Inspect a Swarm service | `machine.swarm.service.inspect` | `dms machine swarm service inspect` (Low) | — | Show a Swarm service's configuration and current version. |
| List Swarm services | `machine.swarm.service.list` | `dms machine swarm service list` (Low) | — | List Swarm services, images, and replica counts. |
| Read Swarm service logs | `machine.swarm.service.logs` | `dms machine swarm service logs` (Medium) | — | Read recent logs from a Swarm service. |
| List Swarm service tasks | `machine.swarm.service.ps` | `dms machine swarm service ps` (Low) | — | Show Swarm service tasks, assigned nodes, and scheduling errors. |
| Roll back a Swarm service | `machine.swarm.service.rollback` | `dms machine swarm service rollback` (High) | — | Rolls back the selected service to its previous spec after checking the expected service version and approved Swarm nodes. |
| Start a Swarm service | `machine.swarm.service.start` | `dms machine swarm service start` (High) | — | Scales a replicated Swarm service to the requested replica count after checking the service version and approved nodes. |
| Stop a Swarm service | `machine.swarm.service.stop` | `dms machine swarm service stop` (High) | — | Scales a replicated Swarm service to zero after checking the service version and approved nodes. |
| Update a Swarm service | `machine.swarm.service.update` | `dms machine swarm service update` (High) | — | Updates a replicated Swarm service to an approved image after checking the service version and approved nodes. |
| Inspect Swarm rollout status | `machine.swarm.service.update-status` | `dms machine swarm service rollout-status` (Low). Alias `update-status` | — | Check a Swarm rollout's image, replicas, and task state. |
| Run an uploaded script | `machine.system.execute-script` | `dms machine system execute-script` (VeryHigh) | — | Runs the approved Bash or Python file within the workflow’s time limit. |
| Inspect operating system information | `machine.system.info` | `dms machine system info` (Safe) | — | Show operating system, kernel, architecture, and time settings. |
| Read the system journal | `machine.system.journal` | `dms machine system journal` (Medium) | — | Read systemd journal entries by unit, boot, or priority. |
| Read kernel events | `machine.system.kernel-events` | `dms machine system kernel-events` (Medium) | — | Read kernel events with time, boot, and category filters. |
| Reboot a machine | `machine.system.reboot` | `dms machine system reboot` (High) | — | Requests a machine reboot and verifies that the Agent reconnects after the boot changes. |
| List host timers and cron jobs | `machine.system.timers` | `dms machine system timers` (Medium) | — | Lists host systemd timers and cron jobs. |
| Inspect system uptime | `machine.system.uptime` | `dms machine system uptime` (Safe) | — | Show how long the machine has been running and its boot ID. |
| List systemd services | `machine.systemctl.list` | `dms machine systemctl list` (Low) | — | List systemd services and their states. |
| Restart a systemd service | `machine.systemctl.restart` | `dms machine systemctl restart` (Medium) | — | Restart a systemd service. |
| Start a systemd service | `machine.systemctl.start` | `dms machine systemctl start` (Medium) | — | Start a systemd service. |
| Inspect systemd service status | `machine.systemctl.status` | `dms machine systemctl status` (Low) | — | Read the current status of a systemd service. |
| Stop a systemd service | `machine.systemctl.stop` | `dms machine systemctl stop` (High) | — | Stop a systemd service. |
| Run a native process | `internal.process` | Parent Machine workflow executes a native command (VeryHigh) | — | Records a native command launched by a parent workflow as a child with its own result and available output. |
| Check a local SSH lease | `internal.ssh.check-lease` | SSH lease reconciliation or security baseline workflow (Medium) | With the parent | Preserves a healthy, unexpired lease and reclaims expired or invalid leases. The baseline passes `CloseSshWhenNoLease=true` to close ordinary SSH when no lease remains. |
| Recover an SSH lease | `internal.ssh.recover-lease` | Internal recovery call for an expected lease ID (Medium) | — | Rechecks the expected lease ID and whether recovery is still due before removing lease resources. |
