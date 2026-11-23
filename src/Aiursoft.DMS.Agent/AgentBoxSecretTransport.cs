using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

internal sealed class AgentBoxSecretTransport(Uri controller, string key) : IBoxSecretTransport
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public string Fingerprint(string name, string value)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes($"dms box secret fingerprint v1\0{name}\0{value}"));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    public async Task<string> TakeSetValueAsync(Guid workflowId, CancellationToken token)
    {
        using var response = await TransferAsync(workflowId, "take-set", null, token);
        return (await response.Content.ReadFromJsonAsync<BoxSecretValue>(cancellationToken: token))?.Value
            ?? throw new InvalidDataException("The Controller returned no Box secret.");
    }

    public async Task PublishRawValueAsync(Guid workflowId, string value, CancellationToken token)
    {
        using var response = await TransferAsync(workflowId, "publish-raw", new BoxSecretValue(value), token);
    }

    private async Task<HttpResponseMessage> TransferAsync(Guid taskId, string action, BoxSecretValue? payload, CancellationToken token)
    {
        if (controller.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Box secret transfer requires an HTTPS Controller.");
        var endpoint = new Uri(controller, $"/api/agent/box-secrets/{taskId:D}/{action}");
        // The Running event can arrive after the first transfer request.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (payload != null) request.Content = JsonContent.Create(payload);
            var response = await Client.SendAsync(request, token);
            if (response.IsSuccessStatusCode) return response;
            var retry = response.StatusCode == HttpStatusCode.Conflict;
            response.Dispose();
            if (!retry) throw new InvalidOperationException("The approved Box secret transfer was rejected or expired.");
            await Task.Delay(500, token);
        }
        throw new InvalidOperationException("The Controller did not acknowledge the running Box secret workflow.");
    }
}
