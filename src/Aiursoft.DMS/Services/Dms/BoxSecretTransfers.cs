using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Cryptography;

namespace Aiursoft.DMS.Services.Dms;

// Sensitive values go through a short-lived process-local store, separate from DMS journals.
public sealed class BoxSecretTransfers(IDataProtectionProvider protection) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly object _gate = new();

    private sealed record TransferKey(string Requester, Guid Request, bool Reveal);
    private sealed record Slot(string? Ciphertext, DateTimeOffset ExpiresAt);

    public bool Stage(string requester, Guid request, bool reveal, string value)
    {
        var key = new TransferKey(requester, request, reveal);
        var expires = DateTimeOffset.UtcNow.AddMinutes(reveal ? 5 : 30);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out _)) return false;
            var entry = new Slot(Protector(key).Protect(value), expires);
            _cache.Set(key, entry, Options(expires));
            return _cache.TryGetValue(key, out _);
        }
    }

    public string? Take(string requester, Guid request, bool reveal)
    {
        var key = new TransferKey(requester, request, reveal);
        lock (_gate)
        {
            if (!_cache.TryGetValue<Slot>(key, out var slot) || slot?.Ciphertext == null) return null;
            // Keep a tombstone until expiry so a replay cannot republish the value.
            _cache.Set(key, slot with { Ciphertext = null }, Options(slot.ExpiresAt));
            try { return Protector(key).Unprotect(slot.Ciphertext); }
            catch (CryptographicException) { return null; }
        }
    }

    private IDataProtector Protector(TransferKey key) =>
        protection.CreateProtector("DMS.BoxSecretTransfer.v1", key.Requester, key.Request.ToString("D"), key.Reveal ? "raw" : "set");

    private static MemoryCacheEntryOptions Options(DateTimeOffset expires) =>
        new() { AbsoluteExpiration = expires, Size = 1, Priority = CacheItemPriority.NeverRemove };

    internal const string IndependentApprovalSuffix = "-as-root";

    internal static bool RequiresIndependentApproval(string definition) =>
        definition == MachineWorkflows.BoxGetSecretRaw.Name ||
        definition.EndsWith(IndependentApprovalSuffix, StringComparison.Ordinal);

    internal static bool IndependentlyApproved(LockboxRequest box) =>
        box.Decision == DecisionState.Approved && !box.Automatic && box.ApproverId != null && box.ApproverId != box.RequesterId;

    public void Dispose() => _cache.Dispose();
}
