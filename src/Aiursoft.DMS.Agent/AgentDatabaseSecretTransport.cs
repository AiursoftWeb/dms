using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;

namespace Aiursoft.DMS.Agent;

internal sealed class AgentDatabaseSecretTransport(Uri controller, string key) : IDatabaseSecretTransport
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public async Task<Dictionary<string, string>> TakeUpsertSecretsAsync(Guid workflowId, CancellationToken token)
    {
        if (controller.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Database secret transfer requires an HTTPS Controller.");
        var endpoint = new Uri(controller, $"/api/agent/database-secrets/{workflowId:D}/take");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await Client.SendAsync(request, token);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<DatabaseUpsertSecretsPayload>(cancellationToken: token);
                return payload?.Secrets ?? throw new InvalidDataException("The Controller returned no database secrets.");
            }
            if (response.StatusCode != HttpStatusCode.Conflict)
                throw new InvalidOperationException("The approved database secret transfer was rejected or expired.");
            await Task.Delay(500, token);
        }
        throw new InvalidOperationException("The Controller did not acknowledge the running database upsert workflow.");
    }
}
