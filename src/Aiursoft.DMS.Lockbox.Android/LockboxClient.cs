using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class LockboxClient(Uri server, Func<CancellationToken, Task<string>> tokenProvider)
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    })
    {
        Timeout = TimeSpan.FromSeconds(35)
    };

    internal static HttpClient SharedHttp => Http;

    public Uri Server => server;

    public static Uri? ParseServer(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0) return null;
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               uri.Host.Length > 0 &&
               uri.UserInfo.Length == 0 &&
               uri.AbsolutePath == "/" &&
               uri.Query.Length == 0 &&
               uri.Fragment.Length == 0
            ? uri
            : null;
    }

    public static bool IsPersonalKey(string value) =>
        value.Length == 69 &&
        value.StartsWith("dmsu_", StringComparison.Ordinal) &&
        value[5..].All(char.IsAsciiHexDigit);

    public static async Task<MobileConfigDto?> DiscoverConfigAsync(Uri server, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "/api/v1/config"));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadHttpErrorAsync(response, deadline.Token);
        }
        return await response.Content.ReadFromJsonAsync(ClientJson.Default.MobileConfigDto, deadline.Token);
    }

    public static async Task<LocalAuthDto> LoginLocalAsync(Uri server, string identity, string password, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "/api/v1/auth/local/login"));
        request.Content = JsonContent.Create(new LocalLoginPayload(identity, password), ClientJson.Default.LocalLoginPayload);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadHttpErrorAsync(response, token);
        }
        return await response.Content.ReadFromJsonAsync(ClientJson.Default.LocalAuthDto, token)
            ?? throw new InvalidDataException("服务器未返回登录令牌。");
    }

    public static async Task<LocalAuthDto> RegisterLocalAsync(Uri server, string email, string password, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "/api/v1/auth/local/register"));
        request.Content = JsonContent.Create(new LocalRegisterPayload(email, password), ClientJson.Default.LocalRegisterPayload);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadHttpErrorAsync(response, token);
        }
        return await response.Content.ReadFromJsonAsync(ClientJson.Default.LocalAuthDto, token)
            ?? throw new InvalidDataException("服务器未返回注册令牌。");
    }

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
                ? $"lockboxes/review/updates?after={Uri.EscapeDataString(latest.ToString("O", CultureInfo.InvariantCulture))}"
                : "lockboxes/review/updates",
            ClientJson.Default.EnvelopeLockboxReviewUpdate, token);

    public Task<LockboxStatus> StatusAsync(LockboxId id, CancellationToken token) =>
        ReadAsync(HttpMethod.Get, $"lockboxes/{id}/status", ClientJson.Default.EnvelopeLockboxStatus, token);

    public async Task DecideAsync(LockboxId id, bool approve, string reason, CancellationToken token)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"lockboxes/{id}/decision", token);
        request.Content = JsonContent.Create(new Decision(approve, reason), ClientJson.Default.Decision);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadHttpErrorAsync(response, token);
        }
    }

    private async Task<T> ReadAsync<T>(HttpMethod method, string path,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<Envelope<T>> type, CancellationToken token) where T : class
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Http.Timeout);
        using var request = await CreateRequestAsync(method, path, deadline.Token);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw await ReadHttpErrorAsync(response, deadline.Token);
        }
        var payload = await response.Content.ReadFromJsonAsync(type, deadline.Token);
        return payload?.Data ?? throw new InvalidDataException("DMS 未返回有效数据。");
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken token)
    {
        var bearer = await tokenProvider(token);
        var request = new HttpRequestMessage(method, new Uri(server, "/api/v1/dms/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return request;
    }

    private static async Task<LockboxHttpException> ReadHttpErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        string? message = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(token);
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var lower) && lower.ValueKind == JsonValueKind.String)
                    message = lower.GetString();
                else if (doc.RootElement.TryGetProperty("Message", out var upper) && upper.ValueKind == JsonValueKind.String)
                    message = upper.GetString();
            }
        }
        catch
        {
            // Ignore non-JSON error payloads.
        }
        return new LockboxHttpException(response.StatusCode, message);
    }
}

internal sealed class LockboxHttpException(HttpStatusCode status, string? serverMessage = null)
    : Exception(serverMessage ?? $"HTTP {(int)status}")
{
    public HttpStatusCode Status { get; } = status;
    public string? ServerMessage { get; } = serverMessage;
}

internal sealed record Envelope<T>(T? Data);
internal sealed record Decision(bool Approve, string Reason);
internal sealed record LocalLoginPayload(string EmailOrUserName, string Password);
internal sealed record LocalRegisterPayload(string Email, string Password);

internal sealed record MobileConfigDto(
    string? AuthenticationMode,
    bool AllowRegistration,
    bool SupportsPersonalApiKey,
    string? Authority,
    string? ClientId,
    string? RedirectUri,
    List<string>? Scopes);

internal sealed record LocalAuthDto(
    string? AccessToken,
    DateTimeOffset ExpiresAt,
    string? DisplayName,
    string? Message);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Envelope<CurrentUser>))]
[JsonSerializable(typeof(Envelope<LockboxReviewPage>))]
[JsonSerializable(typeof(Envelope<LockboxHistoryPage>))]
[JsonSerializable(typeof(Envelope<LockboxReviewUpdate>))]
[JsonSerializable(typeof(Envelope<LockboxStatus>))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(LocalLoginPayload))]
[JsonSerializable(typeof(LocalRegisterPayload))]
[JsonSerializable(typeof(MobileConfigDto))]
[JsonSerializable(typeof(LocalAuthDto))]
internal sealed partial class ClientJson : JsonSerializerContext;
