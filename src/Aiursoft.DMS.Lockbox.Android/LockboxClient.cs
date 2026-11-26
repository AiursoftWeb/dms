using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class LockboxClient(Uri server, string key)
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    public static Uri? ParseServer(string value) => Uri.TryCreate(value.Trim(), UriKind.Absolute, out var server) &&
        server.Scheme == Uri.UriSchemeHttps && server.Host.Length > 0 && server.UserInfo.Length == 0 &&
        server.AbsolutePath == "/" && server.Query.Length == 0 && server.Fragment.Length == 0 ? server : null;

    public static bool IsPersonalKey(string value) => value.Length == 69 &&
        value.StartsWith("dmsu_", StringComparison.Ordinal) && value[5..].All(char.IsAsciiHexDigit);

    public Task<CurrentUser> VerifyAsync(CancellationToken token) =>
        ReadAsync(HttpMethod.Get, "access/identity", ClientJson.Default.EnvelopeCurrentUser, token);

    public Task<LockboxReviewPage> QueueAsync(int page, CancellationToken token) =>
        ReadAsync(HttpMethod.Get, FormattableString.Invariant($"lockboxes/review?page={page}"),
            ClientJson.Default.EnvelopeLockboxReviewPage, token);

    public Task<LockboxHistoryPage> HistoryAsync(int page, CancellationToken token) =>
        ReadAsync(HttpMethod.Get, FormattableString.Invariant($"lockboxes/review/history?page={page}"),
            ClientJson.Default.EnvelopeLockboxHistoryPage, token);

    public Task<LockboxReviewUpdate> WaitForUpdateAsync(DateTime? after, CancellationToken token) =>
        ReadAsync(HttpMethod.Get, after is { } latest
                ? $"lockboxes/review/updates?after={Uri.EscapeDataString(latest.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}"
                : "lockboxes/review/updates", ClientJson.Default.EnvelopeLockboxReviewUpdate, token);

    public Task<LockboxStatus> StatusAsync(LockboxId id, CancellationToken token) =>
        ReadAsync(HttpMethod.Get, $"lockboxes/{id}/status", ClientJson.Default.EnvelopeLockboxStatus, token);

    public async Task DecideAsync(LockboxId id, bool approve, string reason, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, $"lockboxes/{id}/decision");
        request.Content = JsonContent.Create(new Decision(approve, reason), ClientJson.Default.Decision);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) throw new LockboxHttpException(response.StatusCode);
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Envelope<T>> type, CancellationToken token) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Http.Timeout);
        using var request = Request(method, path);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new LockboxHttpException(response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync(type, deadline.Token);
        return payload?.Data ?? throw new InvalidDataException("DMS returned no data.");
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(server, "/api/v1/dms/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}

internal sealed class LockboxHttpException(HttpStatusCode status) : Exception
{
    public HttpStatusCode Status { get; } = status;
}

internal sealed record Envelope<T>(T? Data);
internal sealed record Decision(bool Approve, string Reason);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Envelope<CurrentUser>))]
[JsonSerializable(typeof(Envelope<LockboxReviewPage>))]
[JsonSerializable(typeof(Envelope<LockboxHistoryPage>))]
[JsonSerializable(typeof(Envelope<LockboxReviewUpdate>))]
[JsonSerializable(typeof(Envelope<LockboxStatus>))]
[JsonSerializable(typeof(Decision))]
internal sealed partial class ClientJson : JsonSerializerContext;
