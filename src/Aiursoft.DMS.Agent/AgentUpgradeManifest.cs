using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

internal static class AgentUpgradeManifest
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static Task<AgentUpgradeInput> GetAsync(Uri controller, string key, MachineWorkflowContext context, CancellationToken token) =>
        GetAsync(Client, controller, key, context, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(2), token);

    internal static async Task<AgentUpgradeInput> GetAsync(HttpClient client, Uri controller, string key, MachineWorkflowContext context,
        TimeSpan attemptTimeout, TimeSpan retryDelay, CancellationToken token)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(attemptTimeout);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, "/api/agent/scheduled-upgrade"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.ConnectionClose = true;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                var releases = await response.Content.ReadFromJsonAsync<List<AgentRelease>>(cancellationToken: timeout.Token)
                    ?? throw new InvalidDataException("The Controller returned no Agent release information.");
                var input = new AgentUpgradeInput(releases);
                input.Validate();
                return input;
            }
            catch (Exception exception) when (!token.IsCancellationRequested && AgentUpgrade.IsTransientRequestFailure(exception))
            {
                var reason = exception is OperationCanceledException ? "request timed out" : exception.Message;
                if (attempt == maxAttempts)
                    throw new IOException($"Could not read the Controller's Agent releases after {maxAttempts} attempts: {reason}", exception);
                await context.TraceAsync($"Agent release check attempt {attempt}/{maxAttempts} failed: {reason}. Retrying.", "warning");
                await Task.Delay(retryDelay * attempt, token);
            }
        }
    }
}
