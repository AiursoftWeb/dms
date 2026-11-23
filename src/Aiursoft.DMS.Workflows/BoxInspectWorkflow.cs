using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class BoxInspectWorkflow(string root = "/swarm-vol", string git = "/usr/bin/git", string docker = "/usr/bin/docker")
    : MachineWorkflow<EmptyInput, BoxInspectionResult>(MachineWorkflows.BoxInspect, new EmptyInput())
{
    // Raw Git configuration stays in shell variables. Only allowlisted metadata reaches stdout/trace.
    private const string Inspect = """
        set -euo pipefail
        repository=$1
        git=$2
        allowed_pattern=$3
        docker=${4:-/usr/bin/docker}

        box_git() { "$git" --no-optional-locks -c "safe.directory=$repository" -c core.fsmonitor=false -C "$repository" "$@" 2>/dev/null; }
        fail() { echo 'Cannot read Box Git metadata. No update or deployment was started.' >&2; exit 1; }

        top=$(box_git rev-parse --show-toplevel) || fail
        [ "$top" = "$repository" ] || fail
        revision=$(box_git rev-parse --verify HEAD) || fail
        [[ "$revision" =~ ^([a-f0-9]{40}|[a-f0-9]{64})$ ]] || fail
        branch=$(box_git symbolic-ref --short --quiet HEAD) || branch='[detached]'
        [[ "$branch" =~ ^[a-zA-Z0-9_./-]{1,128}$ ]] || branch='[detached or unrecognized]'

        changed() {
            local result=0
            box_git diff --quiet --no-ext-diff --no-textconv --ignore-submodules "$@" -- || result=$?
            case $result in 0) echo false;; 1) echo true;; *) fail;; esac
        }
        tracked=$(changed)
        staged=$(changed --cached)
        private_tracked=false
        box_git ls-files --error-unmatch .env.prod >/dev/null 2>&1 && private_tracked=true

        commit_date=$(box_git log -1 --format='%cI' 2>/dev/null || echo '[unknown]')
        commit_subject=$(box_git log -1 --format='%s' 2>/dev/null || echo '[no message]')

        mapfile -d '' -t origins < <(box_git config --null --get-all remote.origin.url)
        origin_count="${#origins[@]}"
        official_origin=false
        origin_lines=()

        for origin in "${origins[@]}"; do
            value=${origin,,}
            scheme='[unrecognized]'; host='[unrecognized]'; port='[default]'; name='[unrecognized]'; userinfo=false; official=false
            if [[ "$value" =~ ^(https|ssh)://([^/?#]+) ]]; then
                scheme=${BASH_REMATCH[1]}
                authority=${BASH_REMATCH[2]}
                [[ "$authority" != *@* ]] || userinfo=true
                authority=${authority##*@}
                if [[ "$authority" =~ ^(gitlab\.aiursoft\.com|ssh\.aiursoft\.com|github\.com)(:([0-9]{1,5}))?$ ]]; then
                    host=${BASH_REMATCH[1]}
                    port=${BASH_REMATCH[3]:-'[default]'}
                fi
            elif [[ "$value" =~ ^git@(gitlab\.aiursoft\.com|ssh\.aiursoft\.com): ]]; then
                scheme=scp; host=${BASH_REMATCH[1]}; userinfo=true
            fi
            clean=${value%%[?#]*}
            if [[ "$clean" =~ [:/]aiursoft/(boxfor[a-z0-9_-]+)(\.git)?$ ]]; then name=${BASH_REMATCH[1]}; fi
            if [[ "$value" =~ $allowed_pattern ]]; then official=true; official_origin=true; fi
            origin_lines+=("Origin: scheme=$scheme host=$host port=$port repository=$name userInfoPresent=$userinfo official=$official")
        done

        stage_names=()
        declare -A stage_stacks_map
        total_configured_stacks=0
        all_configured_stacks=()

        shopt -s nullglob
        for stage_dir in "$repository"/stage[0-9]*; do
            [ -d "$stage_dir" ] || continue
            sname=$(basename "$stage_dir")
            stage_names+=("$sname")
            sstacks=()
            if [ -d "$stage_dir/stacks" ]; then
                for s in "$stage_dir"/stacks/*; do
                    [ -d "$s" ] || continue
                    bname=$(basename "$s")
                    sstacks+=("$bname")
                    all_configured_stacks+=("$bname")
                done
            fi
            if [ -d "$stage_dir/registry" ] && [ ! -d "$stage_dir/stacks/registry" ]; then
                sstacks+=("registry")
                all_configured_stacks+=("registry")
            fi
            total_configured_stacks=$((total_configured_stacks + ${#sstacks[@]}))
            if [ "${#sstacks[@]}" -gt 0 ]; then
                readarray -t sorted_sstacks < <(printf '%s\n' "${sstacks[@]}" | sort)
                stage_stacks_map["$sname"]="${sorted_sstacks[*]}"
            else
                stage_stacks_map["$sname"]=""
            fi
        done
        shopt -u nullglob
        total_stages="${#stage_names[@]}"

        box_docker() {
            if [ "$docker" = "/usr/bin/docker" ]; then
                "$docker" --host unix:///var/run/docker.sock "$@" 2>/dev/null
            else
                "$docker" "$@" 2>/dev/null
            fi
        }

        swarm_active=false
        swarm_node_role='[none]'
        swarm_node_state='inactive'
        swarm_node_details='Docker daemon is not running or socket /var/run/docker.sock is unreachable.'

        running_stacks=()
        running_services_total=0
        running_services_healthy=0
        running_services_degraded=0
        degraded_services=()

        secrets_count=0
        secrets_list=()

        can_run_docker=false
        if [ "$docker" = "/usr/bin/docker" ]; then
            if [ -S /var/run/docker.sock ] && [ -x "$docker" ]; then
                can_run_docker=true
            fi
        elif [ -x "$docker" ]; then
            can_run_docker=true
        fi

        if [ "$can_run_docker" = "true" ] && box_docker info >/dev/null 2>&1; then
            swarm_state=$(box_docker info --format '{{.Swarm.LocalNodeState}}' 2>/dev/null || echo "inactive")
            if [ "$swarm_state" = "active" ]; then
                swarm_active=true
                swarm_node_role=$(box_docker info --format '{{if .Swarm.ControlAvailable}}manager{{else}}worker{{end}}' 2>/dev/null || echo "node")
                swarm_node_state="active"

                node_id=$(box_docker info --format '{{.Swarm.NodeID}}' 2>/dev/null || echo "")
                if [ -n "$node_id" ] && [ "$swarm_node_role" = "manager" ]; then
                    swarm_node_details=$(box_docker node inspect "$node_id" --format 'ID={{.ID}} Hostname={{.Description.Hostname}} Status={{.Status.State}} Availability={{.Spec.Availability}} ManagerStatus={{if .ManagerStatus}}{{.ManagerStatus.Reachability}}{{if .ManagerStatus.Leader}} (Leader){{end}}{{else}}none{{end}}' 2>/dev/null || echo "NodeID=$node_id")
                else
                    swarm_node_details="NodeID=$node_id Role=$swarm_node_role State=active"
                fi

                while IFS=$'\t' read -r s_name s_svc_count; do
                    [ -n "$s_name" ] || continue
                    running_stacks+=("$s_name")
                done < <(box_docker stack ls --format '{{.Name}}\t{{.Services}}' 2>/dev/null || true)

                while IFS=$'\t' read -r svc_id svc_name svc_mode svc_replicas svc_image; do
                    [ -n "$svc_name" ] || continue
                    running_services_total=$((running_services_total + 1))
                    if [[ "$svc_replicas" =~ ([0-9]+)/([0-9]+) ]]; then
                        actual="${BASH_REMATCH[1]}"
                        desired="${BASH_REMATCH[2]}"
                        if [ "$desired" -gt 0 ] && [ "$actual" -lt "$desired" ]; then
                            running_services_degraded=$((running_services_degraded + 1))
                            degraded_services+=("$svc_name: $actual/$desired ($svc_image)")
                        else
                            running_services_healthy=$((running_services_healthy + 1))
                        fi
                    else
                        running_services_healthy=$((running_services_healthy + 1))
                    fi
                done < <(box_docker service ls --format '{{.ID}}\t{{.Name}}\t{{.Mode}}\t{{.Replicas}}\t{{.Image}}' 2>/dev/null || true)

                while read -r secret_name; do
                    [ -n "$secret_name" ] || continue
                    secrets_list+=("$secret_name")
                done < <(box_docker secret ls --format '{{.Name}}' 2>/dev/null || true)
                secrets_count="${#secrets_list[@]}"
            else
                swarm_node_details="Docker daemon is running, but Swarm is inactive on this node."
            fi
        fi

        declare -A running_stacks_map
        for rs in "${running_stacks[@]}"; do
            running_stacks_map["$rs"]=1
        done

        active_synced_stacks=()
        configured_not_running=()
        declare -A configured_stacks_map
        for cs in "${all_configured_stacks[@]}"; do
            configured_stacks_map["$cs"]=1
            if [ -n "${running_stacks_map[$cs]:-}" ]; then
                active_synced_stacks+=("$cs")
            else
                configured_not_running+=("$cs")
            fi
        done

        unmanaged_orphan_stacks=()
        for rs in "${running_stacks[@]}"; do
            if [ -z "${configured_stacks_map[$rs]:-}" ]; then
                unmanaged_orphan_stacks+=("$rs")
            fi
        done

        working_tree_state='Clean'
        if [ "$tracked" = "true" ] || [ "$staged" = "true" ]; then
            working_tree_state='Dirty'
        fi

        overall_health='HEALTHY'
        evaluation_notes=()
        if [ "$tracked" = "true" ] || [ "$staged" = "true" ]; then
            overall_health='ATTENTION_REQUIRED'
            evaluation_notes+=('Working tree has uncommitted modifications.')
        fi
        if [ "$private_tracked" = "true" ]; then
            overall_health='ATTENTION_REQUIRED'
            evaluation_notes+=('Private .env.prod is tracked in Git.')
        fi
        if [ "$running_services_degraded" -gt 0 ]; then
            overall_health='DEGRADED'
            evaluation_notes+=("$running_services_degraded service(s) have unready/degraded replicas.")
        fi
        if [ "$swarm_active" = "false" ]; then
            if [ "$overall_health" = "HEALTHY" ]; then
                overall_health='SWARM_INACTIVE'
            fi
            evaluation_notes+=('Docker Swarm is not active on this host.')
        fi
        if [ "${#configured_not_running[@]}" -gt 0 ]; then
            evaluation_notes+=("${#configured_not_running[@]} configured stack(s) are not currently deployed in Swarm.")
        fi

        diag_notes='None'
        if [ "${#evaluation_notes[@]}" -gt 0 ]; then
            IFS='; ' diag_notes="${evaluation_notes[*]}"
            unset IFS
        fi

        echo '================================================================================'
        echo 'BOX INSPECTION REPORT (SUMMARY)'
        echo '================================================================================'
        printf 'Repository: %q\nBranch: %s\nCommit: %s\nWorkingTreeState: %s (TrackedChanges: %s, StagedChanges: %s)\nPrivateEnvironmentTracked: %s\nOriginCount: %d\nOfficialOrigin: %s\nBoxStagesConfigured: %d\nBoxStacksConfigured: %d\nSwarmNodeRole: %s\nSwarmNodeState: %s\nSwarmStacksRunning: %d\nSwarmServicesTotal: %d\nSwarmServicesHealthy: %d\nSwarmServicesDegraded: %d\nSwarmSecretsCount: %d\nOverallHealth: %s\n' \
            "$repository" "$branch" "$revision" "$working_tree_state" "$tracked" "$staged" "$private_tracked" \
            "$origin_count" "$official_origin" "$total_stages" "$total_configured_stacks" \
            "$swarm_node_role" "$swarm_node_state" "${#running_stacks[@]}" "$running_services_total" \
            "$running_services_healthy" "$running_services_degraded" "$secrets_count" "$overall_health"
        echo '================================================================================'
        echo ''
        echo '--------------------------------------------------------------------------------'
        echo 'SECTION 1: GIT REPOSITORY & ENVIRONMENT'
        echo '--------------------------------------------------------------------------------'
        printf 'Repository: %q\nBranch: %s\nCommit: %s\nCommitDate: %s\nCommitSubject: %s\nTrackedChanges: %s\nStagedChanges: %s\nPrivateEnvironmentTracked: %s\nOriginCount: %d\n' \
            "$repository" "$branch" "$revision" "$commit_date" "$commit_subject" "$tracked" "$staged" "$private_tracked" "$origin_count"
        for l in "${origin_lines[@]}"; do
            echo "$l"
        done
        echo ''
        echo '--------------------------------------------------------------------------------'
        echo 'SECTION 2: BOX STAGES & CONFIGURED STACKS (STATIC INVENTORY)'
        echo '--------------------------------------------------------------------------------'
        printf 'ConfiguredStagesCount: %d\nConfiguredStacksTotal: %d\n' "$total_stages" "$total_configured_stacks"
        for sname in "${stage_names[@]}"; do
            stk_str="${stage_stacks_map[$sname]}"
            stk_cnt=$(wc -w <<<"$stk_str")
            if [ "$stk_cnt" -gt 0 ]; then
                echo "  - $sname: $stk_cnt stack(s) [$stk_str]"
            else
                echo "  - $sname: 0 stacks"
            fi
        done
        echo ''
        echo '--------------------------------------------------------------------------------'
        echo 'SECTION 3: SWARM RUNNING STACKS & CODE ALIGNMENT (DYNAMIC INVENTORY)'
        echo '--------------------------------------------------------------------------------'
        printf 'SwarmStatus: %s (Role: %s)\nSwarmStacksCount: %d\n' "$swarm_node_state" "$swarm_node_role" "${#running_stacks[@]}"
        active_str="${active_synced_stacks[*]:-None}"
        not_running_str="${configured_not_running[*]:-None}"
        orphan_str="${unmanaged_orphan_stacks[*]:-None}"
        echo "ActiveSyncedStacks: ${#active_synced_stacks[@]} [$active_str]"
        echo "ConfiguredButNotRunning: ${#configured_not_running[@]} [$not_running_str]"
        echo "UnmanagedOrphanStacks: ${#unmanaged_orphan_stacks[@]} [$orphan_str]"
        echo ''
        echo '--------------------------------------------------------------------------------'
        echo 'SECTION 4: SWARM SERVICES & REPLICA HEALTH'
        echo '--------------------------------------------------------------------------------'
        printf 'TotalServices: %d\nHealthyServices: %d\nDegradedServices: %d\n' "$running_services_total" "$running_services_healthy" "$running_services_degraded"
        if [ "${#degraded_services[@]}" -gt 0 ]; then
            echo 'DegradedServicesDetail:'
            for ds in "${degraded_services[@]}"; do
                echo "  - $ds"
            done
        elif [ "$running_services_total" -gt 0 ]; then
            echo 'All running services have healthy replicas.'
        else
            echo 'No running services detected.'
        fi
        echo ''
        echo '--------------------------------------------------------------------------------'
        echo 'SECTION 5: SWARM SECRETS & CLUSTER RESOURCES'
        echo '--------------------------------------------------------------------------------'
        printf 'SecretsCount: %d\n' "$secrets_count"
        if [ "${#secrets_list[@]}" -gt 0 ]; then
            echo "SecretsList: [${secrets_list[*]}]"
        fi
        echo "SwarmNodeDetails: $swarm_node_details"
        echo ''
        echo '================================================================================'
        echo 'INSPECTION CONCLUSION & NEXT STEPS'
        echo '================================================================================'
        echo "Evaluation: $overall_health"
        echo "DiagnosticNotes: $diag_notes"
        echo 'Snapshot only. No Git pull, deployment, environment-file read or SSH connection was performed.'
        echo '================================================================================'
        """;

    public override async Task<BoxInspectionResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var repository = BoxDeployWorkflow.FindRepository(root);
        await context.CallAsync(new ProcessWorkflow("/bin/bash",
            ["-c", Inspect, "dms-box-inspect", repository, git, BoxDeployWorkflow.OfficialOriginPattern, docker]), token);
        return new BoxInspectionResult();
    }
}
