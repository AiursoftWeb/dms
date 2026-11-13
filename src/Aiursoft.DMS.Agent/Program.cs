using Aiursoft.DMS.Agent;

var endpoint = Environment.GetEnvironmentVariable("DMS_CONTROLLER") ?? throw new InvalidOperationException("DMS_CONTROLLER is required.");
var key = Environment.GetEnvironmentVariable("DMS_MACHINE_KEY") ?? throw new InvalidOperationException("DMS_MACHINE_KEY is required.");
var statePath = Environment.GetEnvironmentVariable("DMS_STATE_PATH") ?? throw new InvalidOperationException("DMS_STATE_PATH must point to persistent private storage.");
var uri = new Uri(endpoint);
if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("Use an HTTP(S) Controller URL without credentials.");
var stopping = new CancellationTokenSource();
var cancel = CreateCancelHandler(stopping);
Console.CancelKeyPress += cancel;
try
{
    using var journal = new TaskJournal(statePath);
    await new AgentClient(uri, key, journal).RunAsync(stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
finally { Console.CancelKeyPress -= cancel; stopping.Dispose(); }

static ConsoleCancelEventHandler CreateCancelHandler(CancellationTokenSource cancellation) =>
    (_, args) => { args.Cancel = true; cancellation.Cancel(); };
