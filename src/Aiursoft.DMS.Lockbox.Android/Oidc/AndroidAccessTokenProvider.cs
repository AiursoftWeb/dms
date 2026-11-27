namespace Aiursoft.DMS.Lockbox.Android.Oidc;

internal sealed class LockboxAuthRequiredException(string message) : InvalidOperationException(message);

internal sealed class AndroidAccessTokenProvider(AndroidKeystoreTokenStore store, OidcPkceClient oidc)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private StoredSession? _session;

    public StoredSession? Current => _session;

    public void SetSession(StoredSession session, bool persist = true)
    {
        _session = session;
        if (persist)
        {
            store.Save(session);
        }
    }

    public StoredSession? LoadSaved()
    {
        _session = store.Load();
        return _session;
    }

    public void Clear(bool keepLastServer = true)
    {
        _session = null;
        store.Clear(keepLastServer);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var current = _session ?? store.Load();
        if (current is null)
        {
            throw new LockboxAuthRequiredException("请先登录 DMS。");
        }

        if (current.Kind == SessionAuthKind.ApiKey)
        {
            _session = current;
            return current.AccessToken;
        }

        if (current.ExpiresAt is { } expires && expires > DateTimeOffset.UtcNow.AddSeconds(45))
        {
            _session = current;
            return current.AccessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            current = _session ?? store.Load();
            if (current is null)
            {
                throw new LockboxAuthRequiredException("请先登录 DMS。");
            }

            if (current.Kind == SessionAuthKind.ApiKey)
            {
                return current.AccessToken;
            }

            if (current.ExpiresAt is { } refreshedExp && refreshedExp > DateTimeOffset.UtcNow.AddSeconds(45))
            {
                return current.AccessToken;
            }

            if (current.Kind == SessionAuthKind.Local)
            {
                Clear();
                throw new LockboxAuthRequiredException("本地登录会话已过期，请重新登录。");
            }

            if (string.IsNullOrWhiteSpace(current.RefreshToken) ||
                string.IsNullOrWhiteSpace(current.TokenEndpoint) ||
                string.IsNullOrWhiteSpace(current.ClientId))
            {
                Clear();
                throw new LockboxAuthRequiredException("OIDC 会话已过期，请重新登录。");
            }

            var refreshed = await oidc.RefreshAsync(new OidcTokenSet(
                current.AccessToken,
                current.RefreshToken,
                current.ExpiresAt ?? DateTimeOffset.MinValue,
                current.TokenEndpoint,
                current.ClientId), cancellationToken);

            var updated = current with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken,
                ExpiresAt = refreshed.ExpiresAt
            };
            _session = updated;
            store.Save(updated);
            return updated.AccessToken;
        }
        catch (LockboxAuthRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Clear();
            throw new LockboxAuthRequiredException($"会话续期失败，请重新登录。（{ex.Message}）");
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
