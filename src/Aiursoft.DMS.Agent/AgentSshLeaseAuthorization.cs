using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Agent;

internal static class AgentSshLeaseAuthorization
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    internal static async Task<SshLeaseAuthorization> GetAsync(Uri controller, string key, Guid taskId, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(controller, $"/api/agent/ssh-leases/{taskId:D}/authorize"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var reply = await Client.SendAsync(request, token);
        if (reply.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("The selected SSH key was revoked or is unavailable. Submit a new request.");
        if (!reply.IsSuccessStatusCode)
            throw new InvalidOperationException($"The Controller refused to authorize the SSH lease (HTTP {(int)reply.StatusCode}).");
        return await reply.Content.ReadFromJsonAsync<SshLeaseAuthorization>(cancellationToken: token)
            ?? throw new InvalidDataException("The Controller returned no SSH key authorization.");
    }
}
