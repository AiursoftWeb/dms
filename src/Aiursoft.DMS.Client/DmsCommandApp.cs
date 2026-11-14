using System.CommandLine;
using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.CommandFramework;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public static class DmsCommandApp
{
    public static NestedCommandApp Create() => new NestedCommandApp()
        .WithFeature(new Group("cluster", [new ActionCommand("get-machines", WorkflowNames.ListMachines), new ActionCommand("add-machine", WorkflowNames.AddMachine)]))
        .WithFeature(new Group("machine", [
            new Group("performance", [new ActionCommand("info", WorkflowNames.Performance)]),
            new Group("systemctl", [new ActionCommand("restart", WorkflowNames.Restart), new ActionCommand("status", WorkflowNames.ServiceStatus)]),
            new Group("agent", [new ActionCommand("status", WorkflowNames.AgentStatus)]),
            new Group("layout", [new ActionCommand("lsblk", WorkflowNames.ListBlockDevices), new ActionCommand("df", WorkflowNames.DiskFree)])]))
        .WithFeature(new Group("lockbox", [new ActionCommand("get", "get"), new ActionCommand("approve", "approve"), new ActionCommand("deny", "deny")]))
        .WithFeature(new Group("operation", [new ActionCommand("get-workflow", "workflow"), new ActionCommand("get-callstack", "callstack"), new ActionCommand("get-trace", "trace"), new ActionCommand("kill-workflow", WorkflowNames.Kill)]));

    private sealed class Group(string name, CommandHandlerBuilder[] children) : NavigationCommandHandlerBuilder
    {
        protected override string Name => name;
        protected override string Description => $"Manage {name}.";
        protected override CommandHandlerBuilder[] GetSubCommandHandlers() => children;
    }

    private sealed class ActionCommand(string name, string action) : ExecutableCommandHandlerBuilder
    {
        protected override string Name => name;
        protected override string Description => action.Contains('.') ? "Submit a Lockbox request. Execution starts only after approval." : "Inspect or decide an existing workflow request.";
        private readonly Option<string?> _server = new("--server") { Description = "Controller URL; defaults to DMS_SERVER." };
        private readonly Option<string?> _key = new("--api-key") { Description = "Personal API Key; defaults to DMS_API_KEY." };
        private readonly Option<bool> _json = new("--json") { Description = "Write the full protocol response as JSON." };
        private readonly Option<Guid> _cluster = new("--cluster-id");
        private readonly Option<Guid[]> _machines = new("--machine-id") { AllowMultipleArgumentsPerToken = true };
        private readonly Option<bool> _all = new("--all-machines");
        private readonly Option<Guid> _id = new("--id");
        private readonly Option<Guid> _request = new("--request-id") { Description = "Reuse this ID only to retry the same submission." };
        private readonly Option<string> _reason = new("--reason");
        private readonly Option<string> _service = new("--service");
        private readonly Option<string> _machineName = new("--name");
        private readonly Option<int> _skip = new("--skip");

        protected override IEnumerable<Option> GetCommandOptions()
        {
            yield return _server; yield return _key; yield return _json;
            if (action.Contains('.'))
            {
                _cluster.Required = true; _reason.Required = true;
                yield return _cluster; yield return _request; yield return _reason;
                if (WorkflowNames.IsMachineWorkflow(action)) { yield return _machines; yield return _all; }
                if (action is WorkflowNames.Restart or WorkflowNames.ServiceStatus) { _service.Required = true; yield return _service; }
                if (action == WorkflowNames.AddMachine) { _machineName.Required = true; yield return _machineName; }
                if (action == WorkflowNames.Kill) { _id.Required = true; yield return _id; }
            }
            else
            {
                _id.Required = true; yield return _id;
                if (action is "approve" or "deny") yield return _reason;
                if (action == "trace") yield return _skip;
            }
        }

        protected override async Task Execute(ParseResult context)
        {
            var endpoint = context.GetValue(_server) ?? Environment.GetEnvironmentVariable("DMS_SERVER") ?? throw new ArgumentException("Set --server or DMS_SERVER.");
            var key = context.GetValue(_key) ?? Environment.GetEnvironmentVariable("DMS_API_KEY") ?? throw new ArgumentException("Set --api-key or DMS_API_KEY.");
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Invalid Controller URL.");
            var services = new ServiceCollection(); services.AddLogging(); services.AddAiurProtocolClient();
            services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
            using var provider = services.BuildServiceProvider();
            var client = new DmsApiClient(provider.GetRequiredService<AiurProtocolClient>(), endpoint, key);
            var id = context.GetValue(_id);
            DmsResponse response;
            if (action.Contains('.'))
            {
                var requestId = context.GetValue(_request);
                response = await client.SubmitAsync(new OperationRequest
                {
                    RequestId = requestId == Guid.Empty ? Guid.NewGuid() : requestId,
                    ClusterId = context.GetValue(_cluster), Workflow = action, MachineIds = (context.GetValue(_machines) ?? []).ToList(),
                    AllMachines = context.GetValue(_all), Reason = context.GetValue(_reason) ?? string.Empty,
                    Service = context.GetValue(_service) ?? string.Empty, MachineName = context.GetValue(_machineName) ?? string.Empty,
                    TargetWorkflowId = action == WorkflowNames.Kill ? id : null
                });
            }
            else response = action switch
            {
                "get" => await client.GetLockboxAsync(id),
                "approve" or "deny" => await client.DecideAsync(id, action == "approve", context.GetValue(_reason) ?? string.Empty),
                "workflow" => await client.GetWorkflowAsync(id),
                "callstack" => await client.CallstackAsync(id),
                "trace" => await client.TraceAsync(id, context.GetValue(_skip)),
                _ => throw new InvalidOperationException("Unsupported command.")
            };
            var output = context.InvocationConfiguration.Output;
            if (!context.GetValue(_json) && action == "callstack" && response.Data is JArray nodes)
            {
                foreach (var line in WorkflowTreeRenderer.Render(nodes, !Console.IsOutputRedirected)) output.WriteLine(line);
            }
            else output.WriteLine(JsonConvert.SerializeObject(response, Formatting.Indented));
        }
    }
}
