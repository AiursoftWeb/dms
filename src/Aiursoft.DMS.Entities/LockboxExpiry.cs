using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Entities;

// Pending requests expire after a fixed lifetime and are purged from the database after the retention window.
public static class LockboxExpiry
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromHours(4);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    // A pending request past its expiry is treated as Expired even before the background sweep persists it.
    public static bool IsDue(LockboxRequest box) =>
        box.Decision == DecisionState.Pending && box.ExpiresAt < DateTime.UtcNow;

    public static DecisionState EffectiveDecision(LockboxRequest box) =>
        IsDue(box) ? DecisionState.Expired : box.Decision;
}
