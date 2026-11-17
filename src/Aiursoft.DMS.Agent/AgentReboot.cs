using Aiursoft.DMS.Workflows.Operations;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

public sealed record RebootHandoff(Guid TaskId, Guid WorkflowId, Guid? ParentId, Guid InstallationId,
    string PreviousBootId, DateTime Deadline, string Phase);

public sealed class AgentReboot(string state, TaskJournal journal, OperationHost? host = null)
{
    private readonly OperationHost _host = host ?? OperationHost.Default;
    private string Marker => Path.Combine(state, "reboot.json");
    public async Task<OperationResult> PrepareAsync(Guid taskId, MachineWorkflowContext context, RebootInput input, CancellationToken token)
    {
        input.Validate();
        if (!OperatingSystem.IsLinux()) throw new NotSupportedException("Reboot confirmation requires Linux.");
        var config = AgentConfiguration.Load(state) ?? throw new InvalidDataException("Reboot requires an installed Agent identity.");
        if (config.InstallationId != journal.InstallationId || config.MachineId == null) throw new InvalidDataException("Installed Agent identity does not match the journal.");
        if (File.Exists(Marker) && Read().Phase == "scheduled") throw new InvalidOperationException("An earlier reboot still needs confirmation.");
        var boot = (await _host.ReadAsync("/proc/sys/kernel/random/boot_id", token)).Trim();
        if (!Guid.TryParse(boot, out _)) throw new InvalidDataException("The kernel did not provide a boot ID.");
        var handoff = new RebootHandoff(taskId, context.Id, context.ParentId, journal.InstallationId, boot, DateTime.UtcNow.AddMinutes(15), "prepared");
        Save(handoff);
        var scheduled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await context.TraceAsync($"Scheduling an approved reboot in {input.DelaySeconds}s. Completion requires the same installation to reconnect with a new boot ID; command exit is not completion.");
            // Mark the effect as uncertain before invoking systemd; losing its reply must never trigger a second reboot.
            Save(handoff with { Phase = "scheduled" }); scheduled = true;
            var result = await _host.RunAsync(new NativeCommand("systemd-run", ["--unit=dms-system-reboot-" + context.Id.ToString("N"),
                "--collect", "--on-active=" + input.DelaySeconds + "s", "/usr/bin/systemctl", "reboot", "--no-wall"], 30), token);
            if (result.ExitCode != 0) throw new IOException("systemd did not confirm reboot scheduling.");
            while (DateTime.UtcNow < handoff.Deadline) await Task.Delay(1000, token);
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        catch (Exception) when (scheduled)
        {
            Save(handoff with { Phase = "uncertain" });
            await context.TraceAsync("Reboot was handed to systemd. Its outcome needs verification until a new boot ID is confirmed; it will not be repeated.", "warning");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
        finally { if (!scheduled) Save(handoff with { Phase = "not-scheduled" }); }
    }
    public async Task ConfirmAsync(CancellationToken token = default)
    {
        if (!File.Exists(Marker)) return;
        var handoff = Read();
        if (handoff.Phase is not ("scheduled" or "uncertain")) return;
        var task = journal.Snapshot().SingleOrDefault(t => t.Id == handoff.TaskId);
        if (task == null || task.State.Terminal()) return;
        var config = AgentConfiguration.Load(state);
        var boot = (await _host.ReadAsync("/proc/sys/kernel/random/boot_id", token)).Trim();
        var recorded = handoff.WorkflowId == task.Id ? task.Definition == OperationsWorkflows.SystemReboot.Name : handoff.ParentId != null;
        var matches = recorded && config?.InstallationId == handoff.InstallationId && journal.InstallationId == handoff.InstallationId
            && config.MachineId != null && handoff.Deadline > DateTime.UtcNow && Guid.TryParse(boot, out _)
            && !boot.Equals(handoff.PreviousBootId, StringComparison.OrdinalIgnoreCase);
        await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = handoff.WorkflowId, ParentId = handoff.ParentId, IsTrace = true,
            Message = matches ? $"The same installation {journal.InstallationId} resumed after reboot: {handoff.PreviousBootId} -> {boot}." : "Reboot is not confirmed: boot/identity/deadline did not match. No reboot was replayed." });
        await journal.ReportAsync(task.Id, new WorkflowEvent { WorkflowId = handoff.WorkflowId, ParentId = handoff.ParentId,
            Definition = OperationsWorkflows.SystemReboot.Name, Version = OperationsWorkflows.SystemReboot.Version,
            State = matches ? RunState.Succeeded : RunState.NeedsVerification,
            Result = matches ? JsonSerializer.Serialize(new OperationResult("Reboot confirmed by a new kernel boot ID on the original Agent installation.")) : "" });
        Save(handoff with { Phase = matches ? "confirmed" : "uncertain" });
    }
    private RebootHandoff Read() => JsonSerializer.Deserialize<RebootHandoff>(File.ReadAllText(Marker)) ?? throw new InvalidDataException("Invalid reboot handoff.");
    private void Save(RebootHandoff handoff)
    {
        Directory.CreateDirectory(state); var temporary = Marker + ".next";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, handoff); stream.Flush(true); }
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, Marker, true);
    }
}
