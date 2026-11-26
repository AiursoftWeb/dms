using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace Aiursoft.DMS.Lockbox.Android.Oidc;

internal enum SessionAuthKind
{
    Oidc,
    Local,
    ApiKey
}

internal sealed record StoredSession(
    string ServerUri,
    SessionAuthKind Kind,
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? TokenEndpoint,
    string? ClientId);

[JsonSerializable(typeof(StoredSession))]
[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
internal sealed partial class StoredSessionJson : JsonSerializerContext;

internal sealed class AndroidKeystoreTokenStore(Context context)
{
    private const string StoreProvider = "AndroidKeyStore";
    private const string Alias = "dms.lockbox.session.v2";
    private const string LegacyAlias = "dms.lockbox.connection.v1";
    private const string Algorithm = "AES/GCM/NoPadding";
    private readonly ISharedPreferences _prefs = context.GetSharedPreferences("dms.lockbox.v2", FileCreationMode.Private)!;
    private readonly ISharedPreferences _legacyPrefs = context.GetSharedPreferences("dms.lockbox", FileCreationMode.Private)!;

    public string? SavedServer =>
        _prefs.GetString("server", null) ?? _legacyPrefs.GetString("server", null);

    public StoredSession? Load()
    {
        var server = LockboxClient.ParseServer(_prefs.GetString("server", null) ?? string.Empty);
        var encrypted = _prefs.GetString("payload", null);
        var iv = _prefs.GetString("iv", null);
        if (server is not null && encrypted is not null && iv is not null)
        {
            var plain = Decrypt(Alias, server.AbsoluteUri, encrypted, iv);
            try
            {
                return JsonSerializer.Deserialize(plain, StoredSessionJson.Default.StoredSession);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        return TryMigrateLegacy();
    }

    public void Save(StoredSession session)
    {
        var server = LockboxClient.ParseServer(session.ServerUri)
            ?? throw new InvalidOperationException("服务器地址无效。");
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, StoredSessionJson.Default.StoredSession);
        try
        {
            var (encrypted, iv) = Encrypt(Alias, server.AbsoluteUri, plain);
            using var editor = _prefs.Edit()!;
            editor.PutString("server", server.AbsoluteUri);
            editor.PutString("iv", iv);
            editor.PutString("payload", encrypted);
            if (!editor.Commit())
            {
                throw new InvalidOperationException("无法保存本机登录会话。");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        ClearLegacy();
    }

    public void SaveLastServer(Uri server)
    {
        using var editor = _prefs.Edit()!;
        editor.PutString("server", server.AbsoluteUri);
        editor.Commit();
    }

    public void Clear(bool keepLastServer = true)
    {
        var lastServer = keepLastServer ? SavedServer : null;
        using var store = Open();
        if (store.ContainsAlias(Alias)) store.DeleteEntry(Alias);
        using var editor = _prefs.Edit()!;
        editor.Clear();
        if (!string.IsNullOrWhiteSpace(lastServer))
        {
            editor.PutString("server", lastServer);
        }
        editor.Commit();
        ClearLegacy();
    }

    private StoredSession? TryMigrateLegacy()
    {
        var legacyServer = LockboxClient.ParseServer(_legacyPrefs.GetString("server", null) ?? string.Empty);
        var legacyKey = _legacyPrefs.GetString("key", null);
        var legacyIv = _legacyPrefs.GetString("iv", null);
        if (legacyServer is null || legacyKey is null || legacyIv is null) return null;

        try
        {
            var plain = Decrypt(LegacyAlias, legacyServer.AbsoluteUri, legacyKey, legacyIv);
            try
            {
                var apiKey = Encoding.UTF8.GetString(plain);
                if (!LockboxClient.IsPersonalKey(apiKey)) return null;
                var migrated = new StoredSession(
                    legacyServer.AbsoluteUri,
                    SessionAuthKind.ApiKey,
                    apiKey,
                    RefreshToken: null,
                    ExpiresAt: null,
                    TokenEndpoint: null,
                    ClientId: null);
                Save(migrated);
                return migrated;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch
        {
            return null;
        }
    }

    private void ClearLegacy()
    {
        try
        {
            using var store = Open();
            if (store.ContainsAlias(LegacyAlias)) store.DeleteEntry(LegacyAlias);
        }
        catch
        {
            // Ignore legacy cleanup errors.
        }
        using var editor = _legacyPrefs.Edit()!;
        editor.Clear();
        editor.Commit();
    }

    private static (string Encrypted, string Iv) Encrypt(string alias, string aad, byte[] plain)
    {
        using var store = Open();
        using var key = store.GetKey(alias, null) ?? GenerateKey(alias);
        using var cipher = Cipher.GetInstance(Algorithm)!;
        cipher.Init(CipherMode.EncryptMode, key);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(aad));
        var encrypted = cipher.DoFinal(plain) ?? throw new InvalidOperationException("加密会话失败。");
        var iv = cipher.GetIV() ?? throw new InvalidOperationException("Android Keystore 未返回 IV。");
        return (Convert.ToBase64String(encrypted), Convert.ToBase64String(iv));
    }

    private static byte[] Decrypt(string alias, string aad, string encryptedBase64, string ivBase64)
    {
        using var store = Open();
        using var key = store.GetKey(alias, null) ?? throw new InvalidOperationException("设备加密密钥不可用。");
        using var cipher = Cipher.GetInstance(Algorithm)!;
        using var parameters = new GCMParameterSpec(128, Convert.FromBase64String(ivBase64));
        cipher.Init(CipherMode.DecryptMode, key, parameters);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(aad));
        return cipher.DoFinal(Convert.FromBase64String(encryptedBase64))
            ?? throw new InvalidOperationException("无法解密本机登录会话。");
    }

    private static KeyStore Open()
    {
        var store = KeyStore.GetInstance(StoreProvider)!;
        store.Load(null);
        return store;
    }

    private static IKey GenerateKey(string alias)
    {
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, StoreProvider)!;
        using var builder = new KeyGenParameterSpec.Builder(alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        using var specification = builder
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .SetKeySize(256)
            .Build();
        generator.Init(specification);
        return generator.GenerateKey() ?? throw new InvalidOperationException("Android Keystore 无法创建密钥。");
    }
}
