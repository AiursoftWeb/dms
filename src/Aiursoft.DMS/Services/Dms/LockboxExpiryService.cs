using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.DMS.Services.Dms;

// Persists the Expired decision for pending requests past their lifetime and purges
// expired requests after the retention window so stale audit rows do not accumulate.
public sealed class LockboxExpiryService(IServiceScopeFactory scopes, ControllerGate gate, ILogger<LockboxExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Lockbox expiry sweep failed; expired requests are retried on the next sweep.");
            }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    public async Task SweepAsync(CancellationToken token)
    {
        await gate.RunAsync(async () =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TemplateDbContext>();
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            var now = DateTime.UtcNow;
            var due = await db.Lockboxes.Where(box => box.Decision == DecisionState.Pending && box.ExpiresAt < now)
                .NotCacheable().ToListAsync(token);
            foreach (var box in due)
            {
                box.Decision = DecisionState.Expired;
                box.DecidedAt = now;
                box.DecisionReason = "Expired automatically after 4 hours without a decision.";
                access.Audit("system", "lockbox.expire", box.Id, $"ExpiresAt={box.ExpiresAt:O}");
            }
            await db.SaveChangesAsync(token);

            var stale = await db.Lockboxes.Where(box => box.Decision == DecisionState.Expired &&
                    box.ExpiresAt < now - LockboxExpiry.Retention)
                .NotCacheable().ToListAsync(token);
            if (stale.Count == 0) return true;
            var ids = stale.Select(box => box.Id).ToArray();
            // Enrollment rows reference their Lockbox with a restricting foreign key; the agent
            // re-enrolls from its receipt, so purging the attempt with its expired request is safe.
            var enrollments = await db.AgentEnrollments.Where(enrollment => ids.Contains(enrollment.LockboxId))
                .NotCacheable().ToListAsync(token);
            db.AgentEnrollments.RemoveRange(enrollments);
            db.Lockboxes.RemoveRange(stale);
            foreach (var box in stale) access.Audit("system", "lockbox.purge", box.Id, $"ExpiredAt={box.ExpiresAt:O}");
            await db.SaveChangesAsync(token);
            logger.LogInformation("Lockbox expiry sweep expired {Due} requests and purged {Purged}.", due.Count, stale.Count);
            return true;
        });
    }
}
