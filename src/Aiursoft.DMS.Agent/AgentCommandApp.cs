using System.CommandLine;
using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.AiurProtocol.Exceptions;
using Aiursoft.AiurProtocol.Models;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.CommandFramework;
using Aiursoft.CommandFramework.Framework;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DMS.Agent;

public static class AgentCommandApp
{
    public static NestedCommandApp Create() => new NestedCommandApp().WithFeature(new AgentCommand("join")).WithFeature(new AgentCommand("run")).WithFeature(new AgentCommand("apply-upgrade"));
    private sealed class AgentCommand(string name) : ExecutableCommandHandlerBuilder
    {
        protected override string Name => name;
        protected override string Description => name == "join" ? "Request enrollment using a cluster-scoped join key. Approval is required." : "Run the installed Agent and resume pending enrollment.";
        private readonly Option<Guid> _id = new("--id") { Required = true };
        private readonly Option<string> _controller = new("--controller-url") { Required = true };
        private readonly Option<string> _key = new("--api-key") { Required = true };
        private readonly Option<string> _cluster = new("--cluster-name") { Required = true };
        private readonly Option<string> _state = new("--state-path") { DefaultValueFactory = _ => "/var/lib/aiursoft-dms-agent" };
        private readonly Option<string> _machine = new("--name") { DefaultValueFactory = _ => Environment.MachineName };
        protected override IEnumerable<Option> GetCommandOptions()
        {
            yield return _state;
            if (name == "apply-upgrade") yield return _id;
            if (name == "join") { yield return _controller; yield return _key; yield return _cluster; yield return _machine; }
        }
        public override Command BuildAsCommand()
        {
            var command = base.BuildAsCommand();
            command.SetAction(async context =>
            {
                try { await Execute(context); return 0; }
                catch (Exception exception) when (exception is AiurUnexpectedServerResponseException or AiurHttpResponseException or ArgumentException or IOException or InvalidDataException or JsonException or HttpRequestException or TaskCanceledException)
                {
                    var message = exception switch
                    {
                        AiurUnexpectedServerResponseException protocol => protocol.Response.Message ?? "Controller rejected enrollment.",
                        JsonException => "Invalid Agent state. Restore the existing state before restarting.",
                        _ => exception.Message
                    };
                    var key = context.GetValue(_key);
                    if (!string.IsNullOrEmpty(key)) message = message.Replace(key, "[redacted]", StringComparison.Ordinal);
                    context.InvocationConfiguration.Error.WriteLine("Error: " + message); return 1;
                }
            });
            return command;
        }
        protected override async Task Execute(ParseResult context)
        {
            var state = Path.GetFullPath(context.GetValue(_state)!);
            if (name == "apply-upgrade") { await AgentUpgrade.ApplyAsync(state, context.GetValue(_id)); return; }
            if (name == "run") { await AgentRuntime.RunInstalledAsync(state); return; }
            var controller = AgentConfiguration.ControllerUrl(context.GetValue(_controller)!);
            var cluster = context.GetValue(_cluster)!.Trim();
            var config = AgentConfiguration.Load(state);
            if (config != null && (config.Controller != controller || !string.Equals(config.ClusterName, cluster, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("This Agent already belongs to another Controller or cluster. Its identity has been preserved.");
            using var provider = AgentRuntime.Services();
            var http = provider.GetRequiredService<AiurProtocolClient>();
            if (config != null && config.MachineId == null)
            {
                try
                {
                    var status = (await new EnrollmentClient(http, controller, config.ReceiptKey).StatusAsync()).Data!;
                    if (status.State == "Joined") { config = config with { MachineId = status.MachineId, ReceiptKey = "" }; config.Save(state); }
                    else if (status.State is "Denied" or "Unavailable" or "Failed") config = null;
                }
                catch (AiurUnexpectedServerResponseException exception) when (exception.Response.Code == Code.NotFound) { }
                catch (AiurHttpResponseException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized) { }
            }
            if (config?.MachineId != null) { context.InvocationConfiguration.Output.WriteLine($"Already joined: machine {config.MachineId}; installation {config.InstallationId}. Existing credentials and tasks were preserved."); return; }
            if (config == null)
            {
                using var journal = new TaskJournal(Path.Combine(state, "journal"));
                config = new AgentConfiguration(controller, cluster, Guid.NewGuid(), journal.InstallationId, context.GetValue(_machine)!,
                    "dmsr_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), "dmsa_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                config.Save(state);
            }
            var result = (await new EnrollmentClient(http, controller, context.GetValue(_key)!).SubmitAsync(new EnrollmentInput(config.EnrollmentId,
                config.InstallationId, cluster, config.MachineName, config.ReceiptKey, config.AgentKey))).Data!;
            if (result.MachineId != null) { config = config with { MachineId = result.MachineId, ReceiptKey = "" }; config.Save(state); }
            context.InvocationConfiguration.Output.WriteLine($"Enrollment {result.Id}: {result.State}\nLockbox: {result.LockboxId}\nWorkflow: {result.WorkflowId}\nInstallation: {result.InstallationId}");
        }
    }
}
