using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class BoxInspectWorkflow(string root = "/swarm-vol", string git = "/usr/bin/git")
    : MachineWorkflow<EmptyInput, BoxInspectionResult>(MachineWorkflows.BoxInspect, new EmptyInput())
{
    // Raw Git configuration stays in shell variables. Only allowlisted metadata reaches stdout/trace.
    private const string Inspect = """
        set -euo pipefail
        repository=$1
        git=$2
        allowed_pattern=$3
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
        box_git ls-files --error-unmatch .env.prod >/dev/null && private_tracked=true
        printf 'Repository: %q\nBranch: %s\nCommit: %s\nTrackedChanges: %s\nStagedChanges: %s\nPrivateEnvironmentTracked: %s\n' \
            "$repository" "$branch" "$revision" "$tracked" "$staged" "$private_tracked"
        mapfile -d '' -t origins < <(box_git config --null --get-all remote.origin.url)
        printf 'OriginCount: %d\n' "${#origins[@]}"
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
            if [[ "$clean" =~ [:/]aiursoft/(boxforglobal|boxforproart|boxforchina|boxforhl|boxforarchive)(\.git)?$ ]]; then name=${BASH_REMATCH[1]}; fi
            [[ ! "$value" =~ $allowed_pattern ]] || official=true
            printf 'Origin: scheme=%s host=%s port=%s repository=%s userInfoPresent=%s official=%s\n' \
                "$scheme" "$host" "$port" "$name" "$userinfo" "$official"
        done
        echo 'Snapshot only. No Git pull, deployment, environment-file read or SSH connection was performed.'
        """;

    public override async Task<BoxInspectionResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var repository = BoxDeployWorkflow.FindRepository(root);
        await context.CallAsync(new ProcessWorkflow("/bin/bash",
            ["-c", Inspect, "dms-box-inspect", repository, git, BoxDeployWorkflow.OfficialOriginPattern]), token);
        return new BoxInspectionResult();
    }
}
