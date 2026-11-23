using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Agent;

internal static class AgentFrpsDeployToken
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<string> GetAsync(Uri controller, string key, Guid taskId, CancellationToken token)
    {
        var address = new Uri(controller, $"/api/agent/frps-deploy/{taskId:D}/token");
        // The Running event may still be in the Agent's journal when the workflow first asks.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, address);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await Client.SendAsync(request, token);
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                await Task.Delay(500, token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("The Controller did not release the approved FRPS token.");
            var result = await response.Content.ReadFromJsonAsync<FrpsDeployToken>(cancellationToken: token);
            return result?.Token ?? throw new InvalidDataException("The Controller returned no FRPS token.");
        }
        throw new InvalidOperationException("The Controller did not acknowledge the running FRPS deployment.");
    }
}
