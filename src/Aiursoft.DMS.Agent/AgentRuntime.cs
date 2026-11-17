using Aiursoft.AiurProtocol;
using Aiursoft.AiurProtocol.Services;
using Aiursoft.DMS.SDK;
using Microsoft.Extensions.DependencyInjection;

namespace Aiursoft.DMS.Agent;

public static class AgentRuntime
{
    public static ServiceProvider Services()
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddAiurProtocolClient();
        services.AddHttpClient(string.Empty).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services.BuildServiceProvider();
    }
    public static async Task RunInstalledAsync(string state)
    {
        var config = AgentConfiguration.Load(state) ?? throw new InvalidDataException("Run dms-agent join first.");
        using var journal = new TaskJournal(Path.Combine(state, "journal"));
        if (journal.InstallationId != config.InstallationId) throw new InvalidDataException("The Agent configuration and journal identities differ.");
        using var provider = Services();
        var client = new EnrollmentClient(provider.GetRequiredService<AiurProtocolClient>(), config.Controller, config.ReceiptKey);
        var last = "";
        while (config.MachineId == null)
        {
            try
            {
                var status = (await client.StatusAsync()).Data ?? throw new InvalidDataException("Missing enrollment status.");
                if (status.State != last) { Console.WriteLine($"Enrollment {status.Id}: {status.State}; Lockbox {status.LockboxId}; Workflow {status.WorkflowId}"); last = status.State; }
                if (status.State == "Joined") { config = config with { MachineId = status.MachineId, ReceiptKey = "" }; config.Save(state); break; }
                if (status.State is "Denied" or "Unavailable" or "Failed") { Console.Error.WriteLine("Enrollment cannot continue. Ask an administrator and run join again with a valid join key."); return; }
            }
            catch (HttpRequestException) { Console.Error.WriteLine("Controller connection interrupted; enrollment will resume."); }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        config.Save(state);
        await new AgentUpgrade(state, journal).ConfirmAsync();
        await new AgentReboot(state, journal).ConfirmAsync();
        await RunAsync(new Uri(config.Controller), config.AgentKey, journal, state);
    }
    public static async Task RunEnvironmentAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("DMS_CONTROLLER") ?? throw new ArgumentException("DMS_CONTROLLER is required.");
        var key = Environment.GetEnvironmentVariable("DMS_MACHINE_KEY") ?? throw new ArgumentException("DMS_MACHINE_KEY is required.");
        var path = Environment.GetEnvironmentVariable("DMS_STATE_PATH") ?? throw new ArgumentException("DMS_STATE_PATH must point to persistent private storage.");
        using var journal = new TaskJournal(path);
        await RunAsync(new Uri(AgentConfiguration.ControllerUrl(endpoint)), key, journal);
    }
    private static async Task RunAsync(Uri controller, string key, TaskJournal journal, string? installedState = null)
    {
        using var stopping = new CancellationTokenSource();
        var cancel = CreateCancelHandler(stopping);
        Console.CancelKeyPress += cancel;
        try { await new AgentClient(controller, key, journal, installedState: installedState).RunAsync(stopping.Token); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        finally { Console.CancelKeyPress -= cancel; }
    }
    private static ConsoleCancelEventHandler CreateCancelHandler(CancellationTokenSource stopping) =>
        (_, args) => { args.Cancel = true; stopping.Cancel(); };
}
