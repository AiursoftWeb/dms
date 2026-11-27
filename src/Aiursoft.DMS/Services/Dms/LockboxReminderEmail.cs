using System.Net;
using System.Net.Mail;
using Aiursoft.DMS.Contracts;
using Microsoft.Extensions.Options;

namespace Aiursoft.DMS.Services.Dms;

public sealed record LockboxReminderSummary(LockboxId Id, string Requester, string Operation, RiskLevel Risk,
    DateTime CreatedAt, DateTime ExpiresAt, int? TargetCount);

public interface ILockboxReminderEmail
{
    Task SendAsync(MailAddress recipient, LockboxReminderSummary summary, CancellationToken token);
}

public sealed class SmtpLockboxReminderEmail(IOptions<LockboxEmailOptions> options) : ILockboxReminderEmail
{
    public async Task SendAsync(MailAddress recipient, LockboxReminderSummary summary, CancellationToken token)
    {
        var settings = options.Value;
        var (subject, body) = Format(summary, new Uri(settings.ControllerOrigin));
        using var message = new MailMessage(new MailAddress(settings.From), recipient);
        message.Subject = subject;
        message.Body = body;
        using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort);
        smtp.EnableSsl = true;
        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
            smtp.Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await smtp.SendMailAsync(message, deadline.Token);
    }

    internal static (string Subject, string Body) Format(LockboxReminderSummary summary, Uri origin)
    {
        var operation = OneLine(summary.Operation);
        var requester = OneLine(summary.Requester);
        var detail = new Uri(origin, $"/Dms/Lockbox/{summary.Id.Value:D}");
        var targets = summary.TargetCount is { } count
            ? FormattableString.Invariant($"{count} machine(s) / {count} 台机器")
            : "Unavailable / 请登录查看";
        var body = FormattableString.Invariant($"""
            This Lockbox request has been pending for at least 15 minutes.
            此 Lockbox 请求已等待审批至少 15 分钟。

            Request / 审批单：{summary.Id}
            Requester / 申请人：{requester}
            Operation / 操作：{operation}
            Risk / 风险：{summary.Risk}
            Submitted / 提交时间：{summary.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC
            Expires / 过期时间：{summary.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC
            Target scope / 目标范围：{targets}

            Sign in to review the full request / 登录查看完整申请：
            {detail}
            """);
        return ($"[DMS Lockbox] [{summary.Risk}] {operation} — 待审批", body);
    }

    private static string OneLine(string value) =>
        new(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
}
