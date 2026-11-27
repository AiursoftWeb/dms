using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static partial class SwarmOperations
{
    // ReSharper disable NotAccessedPositionalProperty.Global
    public sealed record EnvironmentEntrySummary(string Key, string? Value, bool Redacted);
    // ReSharper restore NotAccessedPositionalProperty.Global

    private static readonly string[] SensitiveKeyMarkers =
    [
        "PASSWORD", "PASSWD", "PWD", "SECRET", "TOKEN", "KEY", "AUTH", "CREDENTIAL",
        "CERT", "COOKIE", "SESSION", "JWT", "BEARER", "CONNECTIONSTRING", "CONNECTION_STRING",
        "DSN", "WEBHOOK", "ENCRYPTION", "PGPASS", "PRIVATE", "SAS", "PAT"
    ];

    private static readonly string[] SensitiveValuePrefixes =
    [
        "glpat-", "ghp_", "gho_", "ghs_", "github_pat_", "sk-", "xoxb-", "xoxp-",
        "AIza", "AKIA", "ASIA", "eyJ", "Bearer ", "Basic ", "-----BEGIN "
    ];

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
        var spec = service.GetProperty("Spec");
        var task = spec.GetProperty("TaskTemplate");
        var container = task.GetProperty("ContainerSpec");
        return new
        {
            Id = service.GetProperty("ID").GetString(),
            Version = service.GetProperty("Version").GetProperty("Index").GetInt64(),
            Name = spec.GetProperty("Name").GetString(),
            Image = container.GetProperty("Image").GetString(),
            ForceUpdate = task.TryGetProperty("ForceUpdate", out var forceUpdate) && forceUpdate.ValueKind == JsonValueKind.Number
                ? forceUpdate.GetInt64()
                : 0L,
            Mode = spec.GetProperty("Mode"),
            UpdateConfig = spec.TryGetProperty("UpdateConfig", out var update) ? update : (JsonElement?)null,
            RollbackConfig = spec.TryGetProperty("RollbackConfig", out var rollback) ? rollback : (JsonElement?)null,
            Placement = task.TryGetProperty("Placement", out var placement) ? placement : (JsonElement?)null,
            HealthcheckConfigured = container.TryGetProperty("Healthcheck", out _),
            Mounts = container.TryGetProperty("Mounts", out var mounts) ? mounts : (JsonElement?)null,
            UpdateStatus = service.TryGetProperty("UpdateStatus", out var status) ? status : (JsonElement?)null,
            PreviousSpec = service.TryGetProperty("PreviousSpec", out var previous)
                ? new
                {
                    Image = previous.GetProperty("TaskTemplate").GetProperty("ContainerSpec").GetProperty("Image").GetString(),
                    Mode = previous.GetProperty("Mode"),
                    Mounts = previous.GetProperty("TaskTemplate").GetProperty("ContainerSpec").TryGetProperty("Mounts", out var previousMounts) ? previousMounts : (JsonElement?)null
                }
                : null
        };
    }

    public static async Task<OperationResult> ServiceListAsync(MachineWorkflowContext context, EmptyInput input, AgentEnvironment agent, CancellationToken token)
    {
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("service", "ls", "--format", "{{json .}}"), token);
        return new OperationResult("Swarm services and replica counts collected.");
    }

    public static async Task<OperationResult> ServiceInspectAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        var nodes = await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("node", "ls", "--format", "{{json .}}"), token, traceOutput: false);
        return await OperationOutput.ReportAsync(context, "Service version/specification and node IDs collected; environment and secret contents are excluded.", new { Service = ServiceProjection(service), Nodes = nodes.Output });
    }

    public static async Task<OperationResult> ServiceEnvSummaryAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        var id = service.GetProperty("ID").GetString()!;
        var spec = service.GetProperty("Spec");
        var containerSpec = spec.GetProperty("TaskTemplate").GetProperty("ContainerSpec");
        var specEnv = ExtractEnvArray(containerSpec, "Env");
        var redactedSpecEnv = SummarizeEnvironment(specEnv);

        List<object> runningContainers = [];
        using var http = OperationOutput.Host(agent).CreateDockerClient();
        var tasks = await ReadTasksAsync(http, id, token);
        var runningTasks = tasks
            .Where(t => t.GetProperty("DesiredState").GetString() == "running"
                        && t.GetProperty("Status").GetProperty("State").GetString() == "running")
            .Take(5)
            .ToArray();

        foreach (var task in runningTasks)
        {
            var taskId = task.GetProperty("ID").GetString();
            var nodeId = AssignedNode(task);
            var containerId = task.GetProperty("Status").TryGetProperty("ContainerStatus", out var cs)
                              && cs.ValueKind == JsonValueKind.Object
                              && cs.TryGetProperty("ContainerID", out var cid)
                ? cid.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(containerId))
            {
                runningContainers.Add(new { TaskId = taskId, NodeId = nodeId, ContainerId = (string?)null, LocalContainerInspected = false, Env = Array.Empty<EnvironmentEntrySummary>() });
                continue;
            }

            using var containerResponse = await http.GetAsync($"containers/{Uri.EscapeDataString(containerId)}/json", token);
            if (containerResponse.StatusCode == HttpStatusCode.NotFound)
            {
                runningContainers.Add(new { TaskId = taskId, NodeId = nodeId, ContainerId = containerId, LocalContainerInspected = false, Env = Array.Empty<EnvironmentEntrySummary>() });
                continue;
            }
            containerResponse.EnsureSuccessStatusCode();
            using var containerDoc = JsonDocument.Parse(await containerResponse.Content.ReadAsStringAsync(token));
            var runtimeEnv = containerDoc.RootElement.TryGetProperty("Config", out var cfg)
                ? ExtractEnvArray(cfg, "Env")
                : [];
            runningContainers.Add(new
            {
                TaskId = taskId,
                NodeId = nodeId,
                ContainerId = containerId,
                LocalContainerInspected = true,
                Env = SummarizeEnvironment(runtimeEnv)
            });
        }

        return await OperationOutput.ReportAsync(context,
            "Redacted Swarm service and running task environment summary collected.",
            new
            {
                Service = new
                {
                    Id = id,
                    Name = spec.GetProperty("Name").GetString(),
                    Version = service.GetProperty("Version").GetProperty("Index").GetInt64(),
                    Image = containerSpec.GetProperty("Image").GetString()
                },
                SpecEnv = redactedSpecEnv,
                RunningTaskContainers = runningContainers,
                RedactionPolicy = "Sensitive keys and credential-like values are replaced with [redacted len=N]."
            });
    }

    public static EnvironmentEntrySummary[] SummarizeEnvironment(IEnumerable<string> rawEntries)
    {
        return rawEntries
            .Where(e => !string.IsNullOrEmpty(e))
            .Select(RedactEnvironmentEntry)
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .ToArray();
    }

    public static EnvironmentEntrySummary RedactEnvironmentEntry(string rawEntry)
    {
        var equals = rawEntry.IndexOf('=');
        if (equals < 0)
            return new EnvironmentEntrySummary(rawEntry, null, false);
        var key = rawEntry[..equals];
        var value = rawEntry[(equals + 1)..];
        if (IsSensitiveEnvironmentEntry(key, value))
            return new EnvironmentEntrySummary(key, $"[redacted len={value.Length}]", true);
        var display = value.Length > 256
            ? value[..256] + $"... (truncated, len={value.Length})"
            : value;
        return new EnvironmentEntrySummary(key, display, false);
    }

    public static bool IsSensitiveEnvironmentEntry(string key, string value)
    {
        foreach (var marker in SensitiveKeyMarkers)
        {
            if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        var trimmed = value.TrimStart();
        foreach (var prefix in SensitiveValuePrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        if (EmbeddedCredentialUri().IsMatch(value))
            return true;
        if (ConnectionStringSecret().IsMatch(value))
            return true;
        return false;
    }

    private static string[] ExtractEnvArray(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var env) || env.ValueKind != JsonValueKind.Array)
            return [];
        return env.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    public static async Task<OperationResult> ServicePsAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments("service", "ps", "--no-trunc", "--format", "{{json .}}", input.Service), token);
        return new OperationResult("Service task placement and scheduling errors collected.");
    }

    public static async Task<OperationResult> ServiceLogsAsync(MachineWorkflowContext context, SwarmLogsInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        List<string> args = ["service", "logs", "--timestamps", "--no-trunc", "--tail", input.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (input.Since != null) args.AddRange(["--since", input.Since.Value.ToString("O")]);
        args.Add(input.Service);
        await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments(args.ToArray()), token);
        return new OperationResult("Bounded service logs collected.");
    }

    private static async Task<JsonElement[]> ReadTasksAsync(HttpClient http, string serviceId, CancellationToken token)
    {
        var filter = Uri.EscapeDataString(JsonSerializer.Serialize(new { service = new[] { serviceId } }));
        using var response = await http.GetAsync("tasks?filters=" + filter, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.EnumerateArray().Select(t => t.Clone()).ToArray();
    }

    private static object TaskProjection(JsonElement task) => new
    {
        Id = task.GetProperty("ID").GetString(),
        NodeId = AssignedNode(task),
        DesiredState = task.GetProperty("DesiredState").GetString(),
        State = task.GetProperty("Status").GetProperty("State").GetString(),
        Error = task.GetProperty("Status").TryGetProperty("Err", out var error) ? error.GetString() : null,
        Image = task.GetProperty("Spec").GetProperty("ContainerSpec").GetProperty("Image").GetString(),
        ForceUpdate = task.GetProperty("Spec").TryGetProperty("ForceUpdate", out var fu) && fu.ValueKind == JsonValueKind.Number ? fu.GetInt64() : 0L
    };

    private static string? AssignedNode(JsonElement task) => task.TryGetProperty("NodeID", out var value) ? value.GetString() : null;

    private static bool IsTerminalTaskState(string? state) =>
        state is "shutdown" or "complete" or "failed" or "rejected" or "remove" or "orphaned";

    private static bool Converged(JsonElement service, JsonElement[] tasks)
    {
        var spec = service.GetProperty("Spec");
        var mode = spec.GetProperty("Mode");
        if (!mode.TryGetProperty("Replicated", out var replicated)) return false;
        var replicas = replicated.GetProperty("Replicas").GetInt32();
        var active = tasks.Where(t => t.GetProperty("DesiredState").GetString() == "running").ToArray();
        var image = spec.GetProperty("TaskTemplate").GetProperty("ContainerSpec").GetProperty("Image").GetString();
        var stopped = tasks.Where(t => t.GetProperty("DesiredState").GetString() != "running")
            .All(t => IsTerminalTaskState(t.GetProperty("Status").GetProperty("State").GetString()));
        return active.Length == replicas && stopped && active.All(t => t.GetProperty("Status").GetProperty("State").GetString() == "running"
            && t.GetProperty("Spec").GetProperty("ContainerSpec").GetProperty("Image").GetString() == image)
            && (!service.TryGetProperty("UpdateStatus", out var update) || update.GetProperty("State").GetString() is "completed" or "rollback_completed");
    }

    internal static bool RollingRestartConverged(
        JsonElement service,
        JsonElement[] tasks,
        long previousVersion,
        long targetForceUpdate,
        IReadOnlySet<string> preRestartRunningTaskIds)
    {
        if (service.GetProperty("Version").GetProperty("Index").GetInt64() <= previousVersion) return false;
        var spec = service.GetProperty("Spec");
        var taskTemplate = spec.GetProperty("TaskTemplate");
        var currentForceUpdate = taskTemplate.TryGetProperty("ForceUpdate", out var specFu) && specFu.ValueKind == JsonValueKind.Number
            ? specFu.GetInt64()
            : 0L;
        if (currentForceUpdate != targetForceUpdate) return false;
        if (!spec.GetProperty("Mode").TryGetProperty("Replicated", out var replicated)) return false;
        var replicas = replicated.GetProperty("Replicas").GetInt32();
        if (replicas < 1) return false;
        if (service.TryGetProperty("UpdateStatus", out var updateStatus)
            && updateStatus.ValueKind == JsonValueKind.Object
            && updateStatus.TryGetProperty("State", out var stateEl)
            && stateEl.GetString() is { Length: > 0 } updateState
            && updateState != "completed")
            return false;

        var active = tasks.Where(t => t.GetProperty("DesiredState").GetString() == "running").ToArray();
        var stopped = tasks.Where(t => t.GetProperty("DesiredState").GetString() != "running")
            .All(t => IsTerminalTaskState(t.GetProperty("Status").GetProperty("State").GetString()));
        if (active.Length != replicas || !stopped) return false;

        return active.All(t =>
        {
            var id = t.GetProperty("ID").GetString() ?? "";
            var state = t.GetProperty("Status").GetProperty("State").GetString();
            var taskForceUpdate = t.GetProperty("Spec").TryGetProperty("ForceUpdate", out var fu) && fu.ValueKind == JsonValueKind.Number
                ? fu.GetInt64()
                : 0L;
            return state == "running" && taskForceUpdate == targetForceUpdate && !preRestartRunningTaskIds.Contains(id);
        });
    }

    public static async Task<OperationResult> ServiceUpdateStatusAsync(MachineWorkflowContext context, SwarmServiceInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        using var http = OperationOutput.Host(agent).CreateDockerClient();
        var tasks = await ReadTasksAsync(http, service.GetProperty("ID").GetString()!, token);
        return await OperationOutput.ReportAsync(context, "Rollout identity, replicas and task states collected.", new
        {
            Service = ServiceProjection(service),
            Tasks = tasks.Select(TaskProjection).ToArray(),
            RolloutConverged = Converged(service, tasks),
            ApplicationHealth = "Manager task state does not prove application health on remote nodes; use container health checks or an explicit application probe."
        });
    }

    public static async Task<OperationResult> ServiceRollingRestartAsync(MachineWorkflowContext context, SwarmRollingRestartInput input, AgentEnvironment agent, CancellationToken token)
    {
        input.Validate();
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        var id = service.GetProperty("ID").GetString()!;
        var previousVersion = service.GetProperty("Version").GetProperty("Index").GetInt64();
        var spec = service.GetProperty("Spec");
        if (!spec.GetProperty("Mode").TryGetProperty("Replicated", out var replicated))
            throw new ArgumentException("Rolling restart supports replicated Swarm services.");
        var replicas = replicated.GetProperty("Replicas").GetInt32();
        if (replicas < 1)
            throw new ArgumentException("Cannot rolling-restart a service scaled to 0 replicas.");

        var updateOrder = spec.TryGetProperty("UpdateConfig", out var updateCfg)
            && updateCfg.ValueKind == JsonValueKind.Object
            && updateCfg.TryGetProperty("Order", out var orderEl)
            && orderEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(orderEl.GetString())
            ? orderEl.GetString()!
            : "stop-first";

        if (!string.Equals(updateOrder, "start-first", StringComparison.Ordinal) && !input.AllowDowntime)
            throw new ArgumentException($"Service '{input.Service}' uses update order '{updateOrder}', which causes brief downtime during restart. Re-submit with --allow-downtime to confirm, or switch UpdateConfig.Order to start-first.");

        var taskTemplate = spec.GetProperty("TaskTemplate");
        var currentForceUpdate = taskTemplate.TryGetProperty("ForceUpdate", out var fu) && fu.ValueKind == JsonValueKind.Number
            ? fu.GetInt64()
            : 0L;
        var targetForceUpdate = checked(currentForceUpdate + 1);

        using var http = OperationOutput.Host(agent).CreateDockerClient();
        var initialTasks = await ReadTasksAsync(http, id, token);
        var preRestartRunningTaskIds = initialTasks
            .Where(t => t.GetProperty("DesiredState").GetString() == "running")
            .Select(t => t.GetProperty("ID").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        await OperationOutput.ReportAsync(context, "Service specification before same-image rolling restart.", ServiceProjection(service));

        var specNode = JsonNode.Parse(spec.GetRawText())!.AsObject();
        specNode["TaskTemplate"]!.AsObject()["ForceUpdate"] = targetForceUpdate;

        var affected = false;
        try
        {
            await context.TraceAsync($"Triggering same-image rolling restart for service {id} (version {previousVersion}, ForceUpdate {currentForceUpdate} -> {targetForceUpdate}, order={updateOrder}).");
            token.ThrowIfCancellationRequested();
            affected = true;
            using var response = await http.PostAsJsonAsync($"services/{id}/update?version={previousVersion}", specNode, token);
            response.EnsureSuccessStatusCode();

            var deadline = DateTimeOffset.UtcNow.AddSeconds(input.TimeoutSeconds);
            do
            {
                var current = await ReadServiceAsync(context, id, agent, token);
                var currentTasks = await ReadTasksAsync(http, id, token);
                if (current.TryGetProperty("UpdateStatus", out var updateStatus)
                    && updateStatus.ValueKind == JsonValueKind.Object
                    && updateStatus.TryGetProperty("State", out var stateEl)
                    && stateEl.GetString() is "paused" or "rollback_started" or "rollback_paused" or "rollback_completed")
                    throw new IOException($"Swarm rolling restart halted with UpdateStatus.State='{stateEl.GetString()}'.");

                if (RollingRestartConverged(current, currentTasks, previousVersion, targetForceUpdate, preRestartRunningTaskIds))
                {
                    return await OperationOutput.ReportAsync(context,
                        "Same-image rolling restart converged with replacement tasks running.",
                        new
                        {
                            Service = ServiceProjection(current),
                            PreviousVersion = previousVersion,
                            TargetForceUpdate = targetForceUpdate,
                            UpdateOrder = updateOrder,
                            ReplacedTaskIds = preRestartRunningTaskIds.Order(StringComparer.Ordinal).ToArray(),
                            Tasks = currentTasks.Select(TaskProjection).ToArray()
                        });
                }
                await Task.Delay(1000, token);
            } while (DateTimeOffset.UtcNow < deadline);

            throw new IOException("Rolling restart convergence was not confirmed within the approved timeout.");
        }
        catch (Exception exception) when (affected && exception is not WorkflowNeedsVerificationException)
        {
            await context.TraceAsync("Service ForceUpdate may already have been applied. Inspect rollout status before submitting another restart; no automatic retry or rollback.", "error");
            throw new WorkflowNeedsVerificationException(context.Id);
        }
    }

    public static Task<OperationResult> ServiceStopAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "stop", token);
    public static Task<OperationResult> ServiceStartAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "start", token);
    public static Task<OperationResult> ServiceUpdateAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "update", token);
    public static Task<OperationResult> ServiceRollbackAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, CancellationToken token) => ChangeAsync(context, input, agent, "rollback", token);

    private static async Task<OperationResult> ChangeAsync(MachineWorkflowContext context, SwarmChangeInput input, AgentEnvironment agent, string action, CancellationToken token)
    {
        input.ValidateFor(action);
        var service = await ReadServiceAsync(context, input.Service, agent, token);
        var id = service.GetProperty("ID").GetString()!;
        var version = service.GetProperty("Version").GetProperty("Index").GetInt64();
        if (version != input.ExpectedVersion) throw new InvalidOperationException("Service specification changed after approval. Submit the new version for approval.");
        if (!service.GetProperty("Spec").GetProperty("Mode").TryGetProperty("Replicated", out _)) throw new ArgumentException("This lifecycle workflow supports replicated services; global/job services require a different approved operation.");
        if (action == "update" && input.Image == null) throw new ArgumentException("Service update requires an explicit target image, preferably repository@sha256:digest.");
        using var http = OperationOutput.Host(agent).CreateDockerClient();
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
            using var nodeResponse = await http.GetAsync("nodes", token);
            nodeResponse.EnsureSuccessStatusCode();
            var nodes = JsonNode.Parse(await nodeResponse.Content.ReadAsStringAsync(token))!.AsArray();
            if (!approvedNodes.IsSubsetOf(nodes.Select(n => n!["ID"]!.GetValue<string>()))) throw new ArgumentException("An approved Swarm node no longer exists.");
            if (nodes.Any(n => !approvedNodes.Contains(n!["ID"]!.GetValue<string>()) && n["Spec"]?["Labels"]?[scopeKey]?.GetValue<string>() == "true"))
                throw new ArgumentException("A previous service scope contains another node. Include all affected nodes in a new approval before changing its scope.");
            foreach (var node in nodes.Where(n => approvedNodes.Contains(n!["ID"]!.GetValue<string>())))
            {
                var nodeSpec = node!["Spec"]!.DeepClone();
                nodeSpec["Labels"] ??= new JsonObject();
                nodeSpec["Labels"]![scopeKey] = "true";
                token.ThrowIfCancellationRequested();
                affected = true;
                using var updateNode = await http.PostAsJsonAsync($"nodes/{node["ID"]!.GetValue<string>()}/update?version={node["Version"]!["Index"]!.GetValue<long>()}", nodeSpec, token);
                updateNode.EnsureSuccessStatusCode();
            }
            var template = spec["TaskTemplate"]!.AsObject();
            template["Placement"] ??= new JsonObject();
            var placement = template["Placement"]!.AsObject();
            placement["Constraints"] ??= new JsonArray();
            var constraints = placement["Constraints"]!.AsArray();
            var scopeConstraint = "node.labels." + scopeKey + "==true";
            if (!constraints.Any(c => c?.GetValue<string>() == scopeConstraint)) constraints.Add(scopeConstraint);
            await context.TraceAsync($"Applying {action} to service {id}, expected version {version}; approved node metadata scope: {string.Join(',', input.NodeIds)}.");
            token.ThrowIfCancellationRequested();
            affected = true;
            using var response = await http.PostAsJsonAsync($"services/{id}/update?version={version}", spec, token);
            response.EnsureSuccessStatusCode();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(input.TimeoutSeconds);
            do
            {
                var current = await ReadServiceAsync(context, id, agent, token);
                var currentTasks = await ReadTasksAsync(http, id, token);
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

    [GeneratedRegex(@"://[^/\s]+@", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedCredentialUri();

    [GeneratedRegex(@"(?:^|[;\s])(?:Password|Pwd|Secret|Token|AccountKey|SharedAccessKey|AccessKey|PrivateKey)\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringSecret();
}
