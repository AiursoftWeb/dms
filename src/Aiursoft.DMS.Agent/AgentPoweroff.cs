using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Agent;

public sealed class AgentPoweroff(string state, TaskJournal journal, OperationHost? host = null)
{
    private readonly OperationHost _host = host ?? OperationHost.Default;

    public async Task<OperationResult> PrepareAsync(Guid taskId, MachineWorkflowContext context, PoweroffInput input, CancellationToken token)
    {
        input.Validate();
        if (!OperatingSystem.IsLinux()) throw new NotSupportedException("Poweroff requires Linux.");
        var config = AgentConfiguration.Load(state) ?? throw new InvalidDataException("Poweroff requires an installed Agent identity.");
        if (config.InstallationId != journal.InstallationId || config.MachineId == null)
            throw new InvalidDataException("Installed Agent identity does not match the journal.");

        await context.TraceAsync($"Scheduling an approved poweroff in {input.DelaySeconds}s countdown. The machine will shut down and power off; the Agent will stop and cannot be restarted remotely through DMS.");

        var result = await _host.RunAsync(new NativeCommand("systemd-run", [
            "--unit=dms-system-poweroff-" + context.Id.ToString("N"),
            "--collect",
            "--on-active=" + input.DelaySeconds + "s",
            "/usr/bin/systemctl",
            "poweroff",
            "--no-wall"
        ], 30), token);

        if (result.ExitCode != 0)
            throw new IOException($"systemd did not confirm poweroff scheduling: {result.Error}");

        return new OperationResult($"Poweroff scheduled in {input.DelaySeconds}s countdown. System is powering off.");
    }
}
