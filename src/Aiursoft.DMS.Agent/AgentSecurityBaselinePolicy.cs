using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Agent;

internal static class AgentSecurityBaselinePolicy
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<SecurityBaselinePolicy?> GetAsync(Uri controller, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(controller, "/api/agent/security-baseline"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.ConnectionClose = true;
        using var reply = await Client.SendAsync(request, token);
        if (reply.StatusCode == HttpStatusCode.NoContent) return null;
        reply.EnsureSuccessStatusCode();
        return await reply.Content.ReadFromJsonAsync<SecurityBaselinePolicy>(cancellationToken: token)
            ?? throw new InvalidDataException("The Controller returned no security baseline policy.");
    }
}
