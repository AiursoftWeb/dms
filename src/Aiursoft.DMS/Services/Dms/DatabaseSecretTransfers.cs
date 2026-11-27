using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;
using System.Text.Json;

namespace Aiursoft.DMS.Services.Dms;

// Confidential column values for machine.database.upsert stay in an encrypted process-local store,
// never persisted in DMS Lockbox/Audit/Workflow database records.
public sealed class DatabaseSecretTransfers(IDataProtectionProvider protection) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly object _gate = new();

    private sealed record TransferKey(string Requester, Guid Request);
    private sealed record Slot(string? Ciphertext, DateTimeOffset ExpiresAt);

    public bool Stage(string requester, Guid request, Dictionary<string, string> secrets)
    {
        var key = new TransferKey(requester, request);
        var expires = DateTimeOffset.UtcNow.AddMinutes(30);
        var payload = JsonSerializer.Serialize(secrets);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out _)) return false;
            var entry = new Slot(Protector(key).Protect(payload), expires);
            _cache.Set(key, entry, Options(expires));
            return _cache.TryGetValue(key, out _);
        }
    }

    public Dictionary<string, string>? Take(string requester, Guid request)
    {
        var key = new TransferKey(requester, request);
        lock (_gate)
        {
            if (!_cache.TryGetValue<Slot>(key, out var slot) || slot?.Ciphertext == null) return null;
            _cache.Set(key, slot with { Ciphertext = null }, Options(slot.ExpiresAt));
            try
            {
                var json = Protector(key).Unprotect(slot.Ciphertext);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            }
            catch (CryptographicException) { return null; }
            catch (JsonException) { return null; }
        }
    }

    private IDataProtector Protector(TransferKey key) =>
        protection.CreateProtector("DMS.DatabaseSecretTransfer.v1", key.Requester, key.Request.ToString("D"));

    private static MemoryCacheEntryOptions Options(DateTimeOffset expires) =>
        new() { AbsoluteExpiration = expires, Size = 1, Priority = CacheItemPriority.NeverRemove };

    public void Dispose() => _cache.Dispose();
}
