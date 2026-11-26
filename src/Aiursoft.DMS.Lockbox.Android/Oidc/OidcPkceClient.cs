using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.Content;

namespace Aiursoft.DMS.Lockbox.Android.Oidc;

internal sealed record OidcTokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string TokenEndpoint,
    string ClientId);

internal sealed class OidcPkceClient(HttpClient http, ISharedPreferences preferences)
{
    private const string StateKey = "oidc.state";
    private const string VerifierKey = "oidc.verifier";
    private const string TokenEndpointKey = "oidc.token_endpoint";
    private const string ClientIdKey = "oidc.client_id";
    private const string RedirectUriKey = "oidc.redirect_uri";

    private string _authorizationEndpoint = string.Empty;
    private string _tokenEndpoint = string.Empty;
    private string _clientId = string.Empty;
    private string _redirectUri = string.Empty;
    private IReadOnlyList<string> _scopes = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_authorizationEndpoint) &&
        !string.IsNullOrWhiteSpace(_tokenEndpoint) &&
        !string.IsNullOrWhiteSpace(_clientId) &&
        !string.IsNullOrWhiteSpace(_redirectUri);

    public async Task ConfigureAsync(
        string authority,
        string clientId,
        string redirectUri,
        IReadOnlyList<string> scopes,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(authority.TrimEnd('/') + "/.well-known/openid-configuration", UriKind.Absolute, out var discoveryUri) ||
            discoveryUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OIDC Authority 必须是有效的 HTTPS 地址。");
        }

        using var response = await http.GetAsync(discoveryUri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var authEndpoint = document.RootElement.GetProperty("authorization_endpoint").GetString()
            ?? throw new InvalidOperationException("OIDC 发现文档缺少 authorization_endpoint。");
        var tokenEndpoint = document.RootElement.GetProperty("token_endpoint").GetString()
            ?? throw new InvalidOperationException("OIDC 发现文档缺少 token_endpoint。");

        if (!Uri.TryCreate(authEndpoint, UriKind.Absolute, out var parsedAuth) || parsedAuth.Scheme != Uri.UriSchemeHttps ||
            !Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var parsedToken) || parsedToken.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("OIDC 授权和令牌端点必须使用 HTTPS。");
        }

        _authorizationEndpoint = authEndpoint;
        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _redirectUri = redirectUri;
        _scopes = scopes;
    }

    public Uri CreateAuthorizationUri()
    {
        EnsureConfigured();
        var state = RandomUrlSafe(32);
        var verifier = RandomUrlSafe(64);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var editor = preferences.Edit()!;
        editor.PutString(StateKey, state);
        editor.PutString(VerifierKey, verifier);
        editor.PutString(TokenEndpointKey, _tokenEndpoint);
        editor.PutString(ClientIdKey, _clientId);
        editor.PutString(RedirectUriKey, _redirectUri);
        editor.Commit();

        var values = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["redirect_uri"] = _redirectUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', _scopes),
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        return new Uri($"{_authorizationEndpoint}?{ToQuery(values)}");
    }

    public async Task<OidcTokenSet> CompleteAuthorizationAsync(
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        var query = ParseQuery(callback.Query);
        if (query.TryGetValue("error", out var error))
        {
            var detail = query.TryGetValue("error_description", out var desc) && !string.IsNullOrWhiteSpace(desc)
                ? $"{error}: {desc}"
                : error;
            throw new InvalidOperationException($"OIDC 授权失败：{detail}");
        }

        var expectedState = preferences.GetString(StateKey, string.Empty) ?? string.Empty;
        if (!query.TryGetValue("state", out var state) ||
            string.IsNullOrEmpty(expectedState) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(state),
                Encoding.UTF8.GetBytes(expectedState)))
        {
            throw new InvalidOperationException("OIDC 状态校验失败，请重新发起登录。");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("OIDC 回调中缺少授权码。");
        }

        var tokenEndpoint = preferences.GetString(TokenEndpointKey, string.Empty) ?? string.Empty;
        var clientId = preferences.GetString(ClientIdKey, string.Empty) ?? string.Empty;
        var redirectUri = preferences.GetString(RedirectUriKey, string.Empty) ?? string.Empty;
        var verifier = preferences.GetString(VerifierKey, string.Empty) ?? string.Empty;

        var tokenSet = await RequestTokensAsync(tokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code"] = code,
            ["code_verifier"] = verifier
        }, cancellationToken);

        using var editor = preferences.Edit()!;
        editor.Remove(StateKey);
        editor.Remove(VerifierKey);
        editor.Commit();

        return tokenSet with { TokenEndpoint = tokenEndpoint, ClientId = clientId };
    }

    public Task<OidcTokenSet> RefreshAsync(OidcTokenSet current, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("OIDC 提供方未下发 Refresh Token，请重新登录。");
        }

        return RequestTokensAsync(current.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = current.ClientId,
            ["refresh_token"] = current.RefreshToken
        }, cancellationToken, current.RefreshToken);
    }

    public bool CanResume(OidcTokenSet tokens)
    {
        EnsureConfigured();
        return string.Equals(tokens.TokenEndpoint, _tokenEndpoint, StringComparison.Ordinal) &&
               string.Equals(tokens.ClientId, _clientId, StringComparison.Ordinal);
    }

    private async Task<OidcTokenSet> RequestTokensAsync(
        string tokenEndpoint,
        Dictionary<string, string> form,
        CancellationToken cancellationToken,
        string? existingRefreshToken = null)
    {
        using var response = await http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OIDC 令牌接口返回 {(int)response.StatusCode}。");
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OIDC 响应缺少 access_token。");
        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement) &&
                           refreshElement.ValueKind == JsonValueKind.String
            ? refreshElement.GetString()
            : existingRefreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                        expiresElement.TryGetInt32(out var seconds)
            ? Math.Max(60, seconds)
            : 3600;

        return new OidcTokenSet(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            tokenEndpoint,
            form["client_id"]);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("OIDC 客户端尚未配置。");
        }
    }

    private static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string ToQuery(Dictionary<string, string> values) =>
        string.Join('&', values.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(idx >= 0 ? pair[..idx] : pair);
            var value = idx >= 0 ? Uri.UnescapeDataString(pair[(idx + 1)..]) : string.Empty;
            result[key] = value;
        }
        return result;
    }
}
