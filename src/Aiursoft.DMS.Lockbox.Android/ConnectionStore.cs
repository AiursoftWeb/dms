using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class ConnectionStore(Context context)
{
    private const string StoreProvider = "AndroidKeyStore";
    private const string Alias = "dms.lockbox.connection.v1";
    private const string Algorithm = "AES/GCM/NoPadding";
    private readonly ISharedPreferences _settings = context.GetSharedPreferences("dms.lockbox", FileCreationMode.Private)!;

    public string? Server => _settings.GetString("server", null);

    public LockboxClient? Load()
    {
        var server = LockboxClient.ParseServer(Server ?? "");
        var encrypted = _settings.GetString("key", null);
        var iv = _settings.GetString("iv", null);
        if (server is null || encrypted is null || iv is null) return null;

        using var store = Open();
        using var key = store.GetKey(Alias, null) ?? throw new InvalidOperationException("The device encryption key is unavailable.");
        using var cipher = Cipher.GetInstance(Algorithm)!;
        using var parameters = new GCMParameterSpec(128, Convert.FromBase64String(iv));
        cipher.Init(CipherMode.DecryptMode, key, parameters);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(server.AbsoluteUri));
        var plain = cipher.DoFinal(Convert.FromBase64String(encrypted)) ?? throw new InvalidOperationException("Cannot decrypt the connection.");
        try
        {
            var value = Encoding.UTF8.GetString(plain);
            return LockboxClient.IsPersonalKey(value) ? new LockboxClient(server, value) : null;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(Uri server, string value)
    {
        using var store = Open();
        using var key = store.GetKey(Alias, null) ?? GenerateKey();
        using var cipher = Cipher.GetInstance(Algorithm)!;
        cipher.Init(CipherMode.EncryptMode, key);
        cipher.UpdateAAD(Encoding.UTF8.GetBytes(server.AbsoluteUri));
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            var encrypted = cipher.DoFinal(plain) ?? throw new InvalidOperationException("Cannot encrypt the connection.");
            var iv = cipher.GetIV() ?? throw new InvalidOperationException("Android Keystore did not provide an IV.");
            using var editor = _settings.Edit()!;
            editor.PutString("server", server.AbsoluteUri);
            editor.PutString("iv", Convert.ToBase64String(iv));
            editor.PutString("key", Convert.ToBase64String(encrypted));
            if (!editor.Commit()) throw new InvalidOperationException("Could not save the connection.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Clear()
    {
        using var store = Open();
        if (store.ContainsAlias(Alias)) store.DeleteEntry(Alias);
        using var editor = _settings.Edit()!;
        editor.Clear();
        if (!editor.Commit()) throw new InvalidOperationException("Could not remove the connection.");
    }

    private static KeyStore Open()
    {
        var store = KeyStore.GetInstance(StoreProvider)!;
        store.Load(null);
        return store;
    }

    private static IKey GenerateKey()
    {
        using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, StoreProvider)!;
        using var builder = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        using var specification = builder.SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone).SetKeySize(256).Build();
        generator.Init(specification);
        return generator.GenerateKey() ?? throw new InvalidOperationException("Android Keystore could not create a key.");
    }
}
