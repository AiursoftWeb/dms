using System.Net.Mail;
using Aiursoft.DMS.Authorization;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Entities;
using EFCoreSecondLevelCacheInterceptor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Services.Dms;

public sealed class LockboxEmailReminderService(IServiceScopeFactory scopes, IOptions<LockboxEmailOptions> options,
    ILogger<LockboxEmailReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var reminders = scope.ServiceProvider.GetRequiredService<LockboxEmailReminders>();
                await reminders.SendDueAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Lockbox email reminder scan failed ({ErrorType}); it will retry.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class LockboxEmailReminders(TemplateDbContext db, WorkflowService workflows, AccessService access,
    ILockboxReminderEmail email, ILogger<LockboxEmailReminders> logger)
{
    private const string SentAction = "lockbox.reminder.email.sent";

    public async Task SendDueAsync(DateTime now, CancellationToken token)
    {
        var deadline = now.AddMinutes(-15);
        var due = await db.Lockboxes.AsNoTracking().Where(box => box.Decision == DecisionState.Pending &&
                box.CreatedAt <= deadline && box.ExpiresAt >= now)
            .OrderBy(box => box.CreatedAt).ThenBy(box => box.Id).Select(box => new { box.Id, box.Risk })
            .NotCacheable().ToListAsync(token);
        foreach (var risk in due.GroupBy(box => box.Risk))
        {
            if (!Enum.IsDefined(risk.Key) || risk.Key == RiskLevel.Open) continue;
            var approval = DmsPermissions.Approve(risk.Key);
            var grants = db.UserClaims.Where(claim => claim.ClaimValue == approval).Select(claim => claim.UserId)
                .Union(from membership in db.UserRoles
                    join claim in db.RoleClaims on membership.RoleId equals claim.RoleId
                    where claim.ClaimValue == approval
                    select membership.UserId);
            foreach (var request in risk)
            {
                var sent = db.AuditEvents.Where(entry => entry.ResourceId == request.Id && entry.Action == SentAction)
                    .Select(entry => entry.ActorId);
                var recipients = await db.Users.Where(user => user.EmailConfirmed && user.Email != null &&
                        grants.Contains(user.Id) && !sent.Contains(user.Id))
                    .Select(user => user.Id).NotCacheable().ToListAsync(token);
                foreach (var recipient in recipients)
                {
                    var box = await db.Lockboxes.AsNoTracking().NotCacheable().SingleOrDefaultAsync(item => item.Id == request.Id, token);
                    if (box is null || LockboxExpiry.EffectiveDecision(box) != DecisionState.Pending || !workflows.IsCurrentForReview(box)) break;
                    var user = await db.Users.AsNoTracking().NotCacheable().SingleOrDefaultAsync(person => person.Id == recipient, token);
                    if (user is not { EmailConfirmed: true } || !MailAddress.TryCreate(user.Email, out var address)) continue;
                    try
                    {
                        var principal = await access.RequireViewAsync(recipient);
                        if (!WorkflowService.HasReviewPermission(box, recipient, principal)) continue;
                    }
                    catch (DmsException error) when (error.StatusCode == 403) { continue; }

                    try { await email.SendAsync(address, token); }
                    catch (Exception error) when (!token.IsCancellationRequested)
                    {
                        logger.LogWarning("Lockbox {LockboxId} reminder delivery failed ({ErrorType}); it will retry while pending.", request.Id, error.GetType().Name);
                        continue;
                    }
                    access.Audit(recipient, SentAction, request.Id);
                    await db.SaveChangesAsync(token);
                }
            }
        }
    }
}
