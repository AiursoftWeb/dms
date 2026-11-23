using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Agent;

internal static class AgentUpgradeManifest
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<AgentUpgradeInput> GetAsync(Uri controller, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, "/api/agent/scheduled-upgrade"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.ConnectionClose = true;
        using var response = await Client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var releases = await response.Content.ReadFromJsonAsync<List<AgentRelease>>(cancellationToken: token)
            ?? throw new InvalidDataException("The Controller returned no Agent release information.");
        var input = new AgentUpgradeInput(releases);
        input.Validate();
        return input;
    }
}
