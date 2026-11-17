using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class DockerOperations
{
    public static string[] Arguments(params string[] arguments) => ["--host", "unix:///var/run/docker.sock", .. arguments];
    public static async Task<JsonElement> ReadContainerAsync(MachineWorkflowContext context, string container, AgentEnvironment agent, CancellationToken token)
    {
        new DockerContainerInput(container).Validate();
        var result = await OperationOutput.CommandAsync(context, agent, "docker", Arguments("container", "inspect", container), token, traceOutput: false);
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.GetArrayLength() != 1 || document.RootElement[0].GetProperty("Id").GetString() != container) throw new InvalidDataException("The container identity changed.");
        return document.RootElement[0].Clone();
    }
    public static object ContainerProjection(JsonElement value)
    {
        var state = value.GetProperty("State");
        return new
        {
            Id = value.GetProperty("Id").GetString(), Name = value.GetProperty("Name").GetString(),
            ImageId = value.GetProperty("Image").GetString(), RequestedImage = value.GetProperty("Config").GetProperty("Image").GetString(),
            Status = state.GetProperty("Status").GetString(), Running = state.GetProperty("Running").GetBoolean(),
            ExitCode = state.GetProperty("ExitCode").GetInt32(), OomKilled = state.GetProperty("OOMKilled").GetBoolean(),
            Error = state.GetProperty("Error").GetString(), StartedAt = state.GetProperty("StartedAt").GetString(), FinishedAt = state.GetProperty("FinishedAt").GetString(),
            RestartCount = value.GetProperty("RestartCount").GetInt32(),
            Health = state.TryGetProperty("Health", out var health) ? health.GetProperty("Status").GetString() : "not-configured",
            Mounts = value.GetProperty("Mounts").EnumerateArray().Select(m => new
            {
                Type = m.GetProperty("Type").GetString(), Source = m.GetProperty("Source").GetString(),
                Destination = m.GetProperty("Destination").GetString(), ReadWrite = m.GetProperty("RW").GetBoolean()
            }).ToArray(), SwarmService = SwarmService(value)
        };
    }
    public static string? SwarmService(JsonElement value) =>
        value.GetProperty("Config").TryGetProperty("Labels", out var labels) && labels.ValueKind == JsonValueKind.Object
        && labels.TryGetProperty("com.docker.swarm.service.id", out var service) ? service.GetString() : null;

    public static async Task<OperationResult> InspectAsync(MachineWorkflowContext context, DockerContainerInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var value = await ReadContainerAsync(context, input.Container, agent, token);
        return await OperationOutput.ReportAsync(context, "Container identity and runtime state collected; environment values are excluded.", ContainerProjection(value));
    }
    public static async Task<OperationResult> StatsSampleAsync(MachineWorkflowContext context, SampleInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        return await PerformanceOperations.SampleAsync(context, input, async sampling =>
        {
            var result = await OperationOutput.CommandAsync(context, agent, "docker", Arguments("stats", "--no-stream", "--no-trunc", "--format", "{{json .}}"), sampling, traceOutput: false);
            var rows = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (rows.Length > input.Limit) await context.WarningAsync($"Container sample contains {rows.Length} rows; only {input.Limit} are included.");
            return new { Containers = rows.Take(input.Limit).Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToArray(), TotalObserved = rows.Length };
        }, token);
    }
    public static async Task<OperationResult> EventsAsync(MachineWorkflowContext context, ContainerEventsInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var end = input.Until ?? DateTimeOffset.UtcNow; var start = input.Since ?? end.AddHours(-24); OperationValidation.Window(start, end);
        var result = await OperationOutput.CommandAsync(context, agent, "docker", Arguments("events", "--since", start.ToString("O", CultureInfo.InvariantCulture),
            "--until", end.ToString("O", CultureInfo.InvariantCulture), "--filter", "container=" + input.Container,
            "--format", "{{.TimeNano}}\t{{.Type}}\t{{.Action}}\t{{.Actor.ID}}\t{{index .Actor.Attributes \"exitCode\"}}"), token, traceOutput: false);
        var rows = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return await OperationOutput.ReportAsync(context, "Available Docker event history collected; daemon history is bounded and may not cover the entire requested window.", new { input.Container, Since = start, Until = end, Events = rows.Take(input.Limit).ToArray(), TotalAvailable = rows.Length }, rows.Length <= input.Limit);
    }
    public static async Task<OperationResult> DiagnoseStartupAsync(MachineWorkflowContext context, DockerContainerInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var failures = 0;
        await context.CallAsync(OperationsWorkflows.DockerInspect, input, token);
        try { await context.CallAsync(MachineWorkflows.DockerLogs, new DockerLogsInput(input.Container, 200), token); }
        catch (ChildWorkflowException) { failures++; await context.WarningAsync("Container logs could not be collected; inspect the failed child."); }
        try { await context.CallAsync(OperationsWorkflows.DockerEvents, new ContainerEventsInput(input.Container), token); }
        catch (ChildWorkflowException) { failures++; await context.WarningAsync("Container events could not be collected; inspect the failed child."); }
        var value = await ReadContainerAsync(context, input.Container, agent, token); var state = value.GetProperty("State");
        var health = state.TryGetProperty("Health", out var h) ? h.GetProperty("Status").GetString() : "not-configured";
        return await OperationOutput.ReportAsync(context, "Startup evidence assembled in the child workflow traces.", new
        {
            State = ContainerProjection(value), Reasons = new
            {
                DockerError = state.GetProperty("Error").GetString(), OomKilled = state.GetProperty("OOMKilled").GetBoolean(),
                ExitCode = state.GetProperty("ExitCode").GetInt32(), Health = health,
                Interpretation = "Exit codes, daemon errors and child logs are evidence; Running without a healthcheck does not prove application readiness."
            }
        }, failures == 0);
    }
    public static Task<OperationResult> StopAsync(MachineWorkflowContext context, ContainerActionInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); return ChangeAsync(context, input.Container, "stop", input.TimeoutSeconds, agent, token);
    }
    public static Task<OperationResult> StartAsync(MachineWorkflowContext context, DockerContainerInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); return ChangeAsync(context, input.Container, "start", 30, agent, token);
    }
    public static Task<OperationResult> RestartAsync(MachineWorkflowContext context, ContainerActionInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); return ChangeAsync(context, input.Container, "restart", input.TimeoutSeconds, agent, token);
    }
    private static async Task<OperationResult> ChangeAsync(MachineWorkflowContext context, string container, string action, int timeout, AgentEnvironment agent, CancellationToken token)
    {
        var before = await ReadContainerAsync(context, container, agent, token);
        if (SwarmService(before) != null) throw new ArgumentException("This container is a Swarm task. Use its service lifecycle workflow so Swarm does not replace the stopped task.");
        List<string> args = [action]; if (action != "start") args.AddRange(["--timeout", timeout.ToString(CultureInfo.InvariantCulture)]); args.Add(container);
        await OperationOutput.CommandAsync(context, agent, "docker", Arguments(args.ToArray()), token, change: true, seconds: timeout + 30);
        var after = await ReadContainerAsync(context, container, agent, token);
        var expected = after.GetProperty("State").GetProperty("Running").GetBoolean() == (action != "stop");
        return await OperationOutput.ReportAsync(context, expected ? "Container reached the requested running state." : "Container did not retain the requested state.", ContainerProjection(after), expected);
    }
}
