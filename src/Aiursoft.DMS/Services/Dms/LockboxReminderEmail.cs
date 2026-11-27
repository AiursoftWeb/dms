using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Services.Dms;

public interface ILockboxReminderEmail
{
    Task SendAsync(MailAddress recipient, CancellationToken token);
}

public sealed class SmtpLockboxReminderEmail(IOptions<LockboxEmailOptions> options) : ILockboxReminderEmail
{
    public async Task SendAsync(MailAddress recipient, CancellationToken token)
    {
        var settings = options.Value;
        var queue = new Uri(new Uri(settings.ControllerOrigin), "/Dms/Lockboxes?scope=review");
        using var message = new MailMessage(new MailAddress(settings.From), recipient);
        message.Subject = "DMS Lockbox: approval reminder / 待审批提醒";
        message.Body = $"A Lockbox request has been waiting for at least 15 minutes. Open DMS to review your queue.\n\n有 Lockbox 请求已等待至少 15 分钟。请登录 DMS 查看待审列表。\n\n{queue}";
        using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort);
        smtp.EnableSsl = true;
        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
            smtp.Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await smtp.SendMailAsync(message, deadline.Token);
    }
}
