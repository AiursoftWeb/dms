using Android.Content;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Lockbox.Android.Oidc;

namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class AppSession
{
    private readonly Context _context;
    private readonly AndroidKeystoreTokenStore _tokenStore;
    private readonly OidcPkceClient _oidc;
    private readonly AndroidAccessTokenProvider _tokenProvider;

    public AppSession(Context context)
    {
        _context = context.ApplicationContext!;
        _tokenStore = new AndroidKeystoreTokenStore(_context);
        var oidcPrefs = _context.GetSharedPreferences("dms.lockbox.oidc", FileCreationMode.Private)!;
        _oidc = new OidcPkceClient(LockboxClient.SharedHttp, oidcPrefs);
        _tokenProvider = new AndroidAccessTokenProvider(_tokenStore, _oidc);
    }

    public Uri? Server { get; private set; }
    public SessionAuthKind? AuthKind { get; private set; }
    public CurrentUser? User { get; private set; }
    public LockboxClient? Client { get; private set; }
    public MobileConfigDto? LastConfig { get; private set; }
    public string? SavedServer => _tokenStore.SavedServer;
    public bool IsAuthenticated => Client is not null && Server is not null;

    public string AuthKindLabel => AuthKind switch
    {
        SessionAuthKind.Oidc => "OIDC 单点登录",
        SessionAuthKind.Local => "本地账号",
        SessionAuthKind.ApiKey => "个人 API Key",
        _ => "未连接"
    };

    public bool TryRestoreFromDisk()
    {
        if (IsAuthenticated) return true;
        var saved = _tokenProvider.LoadSaved();
        if (saved is null) return false;
        var server = LockboxClient.ParseServer(saved.ServerUri);
        if (server is null) return false;

        Server = server;
        AuthKind = saved.Kind;
        Client = new LockboxClient(server, _tokenProvider.GetAccessTokenAsync);
        return true;
    }

    public async Task<MobileConfigDto?> ConnectServerAsync(Uri server, CancellationToken token)
    {
        var config = await LockboxClient.DiscoverConfigAsync(server, token);
        Server = server;
        LastConfig = config;
        _tokenStore.SaveLastServer(server);

        if (config is not null &&
            string.Equals(config.AuthenticationMode, "OIDC", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(config.Authority) &&
            !string.IsNullOrWhiteSpace(config.ClientId) &&
            !string.IsNullOrWhiteSpace(config.RedirectUri))
        {
            await _oidc.ConfigureAsync(
                config.Authority,
                config.ClientId,
                config.RedirectUri,
                config.Scopes ?? ["openid", "profile", "email", "offline_access"],
                token);
        }

        return config;
    }

    public bool IsOidcReady =>
        LastConfig is not null &&
        string.Equals(LastConfig.AuthenticationMode, "OIDC", StringComparison.OrdinalIgnoreCase) &&
        _oidc.IsConfigured;

    public Uri CreateOidcAuthorizationUri() => _oidc.CreateAuthorizationUri();

    public async Task<CurrentUser> CompleteOidcSignInAsync(Uri callback, CancellationToken token)
    {
        var server = Server ?? LockboxClient.ParseServer(SavedServer ?? string.Empty)
            ?? throw new InvalidOperationException("未找到待登录的 DMS 服务器地址。");
        var tokens = await _oidc.CompleteAuthorizationAsync(callback, token);
        var session = new StoredSession(
            server.AbsoluteUri,
            SessionAuthKind.Oidc,
            tokens.AccessToken,
            tokens.RefreshToken,
            tokens.ExpiresAt,
            tokens.TokenEndpoint,
            tokens.ClientId);
        return await ActivateAndVerifyAsync(server, session, token);
    }

    public async Task<CurrentUser> SignInLocalAsync(Uri server, string identity, string password, bool register, CancellationToken token)
    {
        var auth = register
            ? await LockboxClient.RegisterLocalAsync(server, identity, password, token)
            : await LockboxClient.LoginLocalAsync(server, identity, password, token);
        if (string.IsNullOrWhiteSpace(auth.AccessToken))
        {
            throw new InvalidOperationException("服务器未返回有效的访问令牌。");
        }

        var session = new StoredSession(
            server.AbsoluteUri,
            SessionAuthKind.Local,
            auth.AccessToken,
            RefreshToken: null,
            auth.ExpiresAt,
            TokenEndpoint: null,
            ClientId: null);
        return await ActivateAndVerifyAsync(server, session, token);
    }

    public async Task<CurrentUser> SignInApiKeyAsync(Uri server, string personalKey, CancellationToken token)
    {
        if (!LockboxClient.IsPersonalKey(personalKey))
        {
            throw new InvalidOperationException("请输入有效的 69 位 DMS 个人 API Key（dmsu_ 开头）。");
        }

        var session = new StoredSession(
            server.AbsoluteUri,
            SessionAuthKind.ApiKey,
            personalKey,
            RefreshToken: null,
            ExpiresAt: null,
            TokenEndpoint: null,
            ClientId: null);
        return await ActivateAndVerifyAsync(server, session, token);
    }

    public async Task<CurrentUser> RefreshIdentityAsync(CancellationToken token)
    {
        var client = Client ?? throw new LockboxAuthRequiredException("请先登录 DMS。");
        var user = await client.VerifyAsync(token);
        User = user;
        return user;
    }

    public void SignOut()
    {
        ReviewNotificationService.StopListening(_context);
        _tokenProvider.Clear(keepLastServer: true);
        Client = null;
        User = null;
        AuthKind = null;
    }

    private async Task<CurrentUser> ActivateAndVerifyAsync(Uri server, StoredSession session, CancellationToken token)
    {
        var candidateClient = new LockboxClient(server, _ => Task.FromResult(session.AccessToken));
        var user = await candidateClient.VerifyAsync(token);

        ReviewNotificationService.StopListening(_context);
        _tokenProvider.SetSession(session, persist: true);
        Server = server;
        AuthKind = session.Kind;
        User = user;
        Client = new LockboxClient(server, _tokenProvider.GetAccessTokenAsync);
        return user;
    }
}
