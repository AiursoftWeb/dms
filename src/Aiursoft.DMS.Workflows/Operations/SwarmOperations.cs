using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class SwarmOperations
{
    public static async Task<JsonElement> ReadServiceAsync(MachineWorkflowContext context, string name, AgentEnvironment agent, CancellationToken token)
    {
        OperationValidation.Name(name);
        var result = await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("service", "inspect", name), token, traceOutput: false);
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.GetArrayLength() != 1) throw new InvalidDataException("Service identity is ambiguous.");
        return document.RootElement[0].Clone();
    }
    public static object ServiceProjection(JsonElement service)
    {
        var spec = service.GetProperty("Spec"); var task = spec.GetProperty("TaskTemplate"); var container = task.GetProperty("ContainerSpec");
        return new
        {
            Id = service.GetProperty("ID").GetString(), Version = service.GetProperty("Version").GetProperty("Index").GetInt64(),
            Name = spec.GetProperty("Name").GetString(), Image = container.GetProperty("Image").GetString(),
            Mode = spec.GetProperty("Mode"), UpdateConfig = spec.TryGetProperty("UpdateConfig", out var update) ? update : (JsonElement?)null,
            RollbackConfig = spec.TryGetProperty("RollbackConfig", out var rollback) ? rollback : (JsonElement?)null,
            Placement = task.TryGetProperty("Placement", out var placement) ? placement : (JsonElement?)null,
            HealthcheckConfigured = container.TryGetProperty("Healthcheck", out _),
            Mounts = container.TryGetProperty("Mounts", out var mounts) ? mounts : (JsonElement?)null,
            UpdateStatus = service.TryGetProperty("UpdateStatus", out var status) ? status : (JsonElement?)null,
            PreviousSpec = service.TryGetProperty("PreviousSpec", out var previous) ? new
            {
                Image = previous.GetProperty("TaskTemplate").GetProperty("ContainerSpec").GetProperty("Image").GetString(),
                Mode = previous.GetProperty("Mode"),
                Mounts = previous.GetProperty("TaskTemplate").GetProperty("ContainerSpec").TryGetProperty("Mounts", out var previousMounts) ? previousMounts : (JsonElement?)null
            } : null
        };
    }
    public static async Task<OperationResult> ServiceListAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("service", "ls", "--format", "{{json .}}"), token);
        return new OperationResult("Swarm services and replica counts collected.");
    }
    public static async Task<OperationResult> ServiceInspectAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var service = await ReadServiceAsync(context, input.Service, agent, token);
        var nodes = await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("node", "ls", "--format", "{{json .}}"), token, traceOutput: false);
        return await OperationOutput.ReportAsync(context, "Service version/specification and node IDs collected; environment and secret contents are excluded.", new { Service = ServiceProjection(service), Nodes = nodes.Output });
    }
    public static async Task<OperationResult> ServicePsAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("service", "ps", "--no-trunc", "--format", "{{json .}}", input.Service), token);
        return new OperationResult("Service task placement and scheduling errors collected.");
    }
    public static async Task<OperationResult> ServiceLogsAsync(MachineWorkflowContext context, SwarmLogsInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); List<string> args = ["service", "logs", "--timestamps", "--no-trunc", "--tail", input.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (input.Since != null) args.AddRange(["--since", input.Since.Value.ToString("O")]); args.Add(input.Service);
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments(args.ToArray()), token);
        return new OperationResult("Bounded service logs collected.");
    }
    private static async Task<JsonElement[]> ReadTasksAsync(HttpClient http, string serviceId, CancellationToken token)
    {
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { service = new[] { serviceId } }));
        using var response = await http.GetAsync("tasks?filters=" + filter, token); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.EnumerateArray().Select(t => t.Clone()).ToArray();
    }
    private static object TaskProjection(JsonElement task) => new
    {
        Id = task.GetProperty("ID").GetString(), NodeId = AssignedNode(task),
        DesiredState = task.GetProperty("DesiredState").GetString(),
        State = task.GetProperty("Status").GetProperty("State").GetString(),
        Error = task.GetProperty("Status").TryGetProperty("Err", out var error) ? error.GetString() : null,
        Image = task.GetProperty("Spec").GetProperty("ContainerSpec").GetProperty("Image").GetString()
    };
    private static string? AssignedNode(JsonElement task) => task.TryGetProperty("NodeID", out var value) ? value.GetString() : null;
    private static bool Converged(JsonElement service, JsonElement[] tasks)
    {
        var spec = service.GetProperty("Spec"); var mode = spec.GetProperty("Mode");
        if (!mode.TryGetProperty("Replicated", out var replicated)) return false;
        var replicas = replicated.GetProperty("Replicas").GetInt32();
        var active = tasks.Where(t => t.GetProperty("DesiredState").GetString() == "running").ToArray();
        var image = spec.GetProperty("TaskTemplate").GetProperty("ContainerSpec").GetProperty("Image").GetString();
        var stopped = tasks.Where(t => t.GetProperty("DesiredState").GetString() != "running").All(t => t.GetProperty("Status").GetProperty("State").GetString() is "shutdown" or "complete" or "failed" or "rejected" or "remove" or "orphaned");
        return active.Length == replicas && stopped && active.All(t => t.GetProperty("Status").GetProperty("State").GetString() == "running"
            && t.GetProperty("Spec").GetProperty("ContainerSpec").GetProperty("Image").GetString() == image)
            && (!service.TryGetProperty("UpdateStatus", out var update) || update.GetProperty("State").GetString() is "completed" or "rollback_completed");
    }
    public static async Task<OperationResult> ServiceUpdateStatusAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate(); var service = await ReadServiceAsync(context, input.Service, agent, token);
        using var http = DockerOperations.ArchiveClient();
        var tasks = await ReadTasksAsync(http, service.GetProperty("ID").GetString()!, token);
        return await OperationOutput.ReportAsync(context, "Rollout identity, replicas and task states collected.", new
        {
            Service = ServiceProjection(service), Tasks = tasks.Select(TaskProjection).ToArray(), RolloutConverged = Converged(service, tasks),
            ApplicationHealth = "Manager task state does not prove application health on remote nodes; use container health checks or an explicit application probe."
        });
    }
    public static Task<OperationResult> ServiceStopAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "stop", token);
    public static Task<OperationResult> ServiceStartAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "start", token);
    public static Task<OperationResult> ServiceUpdateAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "update", token);
    public static Task<OperationResult> ServiceRollbackAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "rollback", token);

    private static async Task<OperationResult> ChangeAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, string action, CancellationToken token)
    {
        input.ValidateFor(action);
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        var id = service.GetProperty("ID").GetString()!; var version = service.GetProperty("Version").GetProperty("Index").GetInt64();
        if (version != input.ExpectedVersion) throw new InvalidOperationException("Service specification changed after approval. Submit the new version for approval.");
        if (!service.GetProperty("Spec").GetProperty("Mode").TryGetProperty("Replicated", out _)) throw new ArgumentException("This lifecycle workflow supports replicated services; global/job services require a different approved operation.");
        if (action == "update" && input.Image == null) throw new ArgumentException("Service update requires an explicit target image, preferably repository@sha256:digest.");
        using var http = DockerOperations.ArchiveClient();
        var tasks = await ReadTasksAsync(http, id, token);
        var approvedNodes = input.NodeIds.ToHashSet(StringComparer.Ordinal);
        if (tasks.Where(t => t.GetProperty("DesiredState").GetString() == "running").Any(t => AssignedNode(t) is { Length: > 0 } node && !approvedNodes.Contains(node)))
            throw new ArgumentException("The service has active tasks outside the approved Swarm node scope.");
        var spec = JsonNode.Parse((action == "rollback" ? service.GetProperty("PreviousSpec") : service.GetProperty("Spec")).GetRawText())!.AsObject();
        await OperationOutput.ReportAsync(context, "Service specification before the approved change; includes the original desired replica count.", ServiceProjection(service));
        if (action is "stop" or "start") spec["Mode"]!["Replicated"]!["Replicas"] = action == "stop" ? 0 : input.Replicas;
        if (action == "update") spec["TaskTemplate"]!["ContainerSpec"]!["Image"] = input.Image;
        var affected = false;
        try
        {
            // A stable per-service label restricts Swarm scheduling to the exact approved node set.
            // It changes only node metadata, preserves other labels and is visible in the approval's NodeIds.
            var scopeKey = "aiursoft.dms.service-scope." + id;
            using var nodeResponse = await http.GetAsync("nodes", token); nodeResponse.EnsureSuccessStatusCode();
            var nodes = JsonNode.Parse(await nodeResponse.Content.ReadAsStringAsync(token))!.AsArray();
            if (!approvedNodes.IsSubsetOf(nodes.Select(n => n!["ID"]!.GetValue<string>()))) throw new ArgumentException("An approved Swarm node no longer exists.");
            if (nodes.Any(n => !approvedNodes.Contains(n!["ID"]!.GetValue<string>()) && n["Spec"]?["Labels"]?[scopeKey]?.GetValue<string>() == "true"))
                throw new ArgumentException("A previous service scope contains another node. Include all affected nodes in a new approval before changing its scope.");
            foreach (var node in nodes.Where(n => approvedNodes.Contains(n!["ID"]!.GetValue<string>())))
            {
                var nodeSpec = node!["Spec"]!.DeepClone(); nodeSpec["Labels"] ??= new JsonObject(); nodeSpec["Labels"]![scopeKey] = "true";
                token.ThrowIfCancellationRequested(); affected = true;
                using var updateNode = await http.PostAsJsonAsync($"nodes/{node["ID"]!.GetValue<string>()}/update?version={node["Version"]!["Index"]!.GetValue<long>()}", nodeSpec, token);
                updateNode.EnsureSuccessStatusCode();
            }
            var template = spec["TaskTemplate"]!.AsObject(); template["Placement"] ??= new JsonObject();
            var placement = template["Placement"]!.AsObject(); placement["Constraints"] ??= new JsonArray();
            var constraints = placement["Constraints"]!.AsArray(); var scopeConstraint = "node.labels." + scopeKey + "==true";
            if (!constraints.Any(c => c?.GetValue<string>() == scopeConstraint)) constraints.Add(scopeConstraint);
            await context.TraceAsync($"Applying {action} to service {id}, expected version {version}; approved node metadata scope: {string.Join(',', input.NodeIds)}.");
            token.ThrowIfCancellationRequested(); affected = true;
            using var response = await http.PostAsJsonAsync($"services/{id}/update?version={version}", spec, token); response.EnsureSuccessStatusCode();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(input.TimeoutSeconds);
            do
            {
                var current = await ReadServiceAsync(context, id, agent, token); var currentTasks = await ReadTasksAsync(http, id, token);
                if (currentTasks.Where(t => t.GetProperty("DesiredState").GetString() == "running").Any(t => AssignedNode(t) is { Length: > 0 } node && !approvedNodes.Contains(node))) throw new IOException("Swarm scheduled a task outside the approved node scope.");
                if (Converged(current, currentTasks))
                    return await OperationOutput.ReportAsync(context, "Requested service rollout converged. Application readiness requires its health/probe evidence.", new { Service = ServiceProjection(current), Tasks = currentTasks.Select(TaskProjection).ToArray(), ApprovedNodeIds = input.NodeIds });
                await Task.Delay(1000, token);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new IOException("Service convergence was not confirmed within the approved timeout.");
        }
        catch (Exception exception) when (affected && exception is not WorkflowNeedsVerificationException)
        {
            await context.TraceAsync("Service/node metadata may already have changed. Inspect rollout and node labels before another request; no automatic retry or rollback.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }
}
