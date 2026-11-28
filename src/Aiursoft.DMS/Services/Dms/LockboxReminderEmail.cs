using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
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
        var (subject, text, html) = Format(summary, new Uri(settings.ControllerOrigin));
        using var message = new MailMessage(new MailAddress(settings.From), recipient);
        message.Subject = subject;
        message.Body = text;
        message.BodyEncoding = Encoding.UTF8;
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(html, Encoding.UTF8, MediaTypeNames.Text.Html));
        using var smtp = new SmtpClient(settings.SmtpHost, settings.SmtpPort);
        smtp.EnableSsl = true;
        if (!string.IsNullOrWhiteSpace(settings.SmtpUsername))
            smtp.Credentials = new NetworkCredential(settings.SmtpUsername, settings.SmtpPassword);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await smtp.SendMailAsync(message, deadline.Token);
    }

    internal static (string Subject, string Text, string Html) Format(LockboxReminderSummary summary, Uri origin)
    {
        var operation = OneLine(summary.Operation);
        var requester = OneLine(summary.Requester);
        var detail = new Uri(origin, $"/Dms/Lockbox/{summary.Id.Value:D}");
        var server = "'" + origin.GetLeftPart(UriPartial.Authority).Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var approve = $"dms lockbox approve --id {summary.Id} --server {server}";
        var deny = $"dms lockbox deny --id {summary.Id} --server {server}";
        var targets = summary.TargetCount is { } count
            ? count == 1 ? "1 machine" : FormattableString.Invariant($"{count} machines")
            : "Unavailable (sign in to view)";
        var submitted = FormattableString.Invariant($"{summary.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        var expires = FormattableString.Invariant($"{summary.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC");
        var text = $"""
            This Lockbox request has been pending for at least 15 minutes.

            Request: {summary.Id}
            Requester: {requester}
            Operation: {operation}
            Risk: {summary.Risk}
            Submitted: {submitted}
            Expires: {expires}
            Target scope: {targets}

            Sign in to review the full request:
            {detail}

            Review the request before running either command:
            To approve, run: {approve}
            To deny, run: {deny}
            """;
        var badgeBackground = summary.Risk switch
        {
            RiskLevel.High or RiskLevel.VeryHigh => "#fce8e6",
            RiskLevel.Medium => "#fff3d6",
            _ => "#e7f4eb"
        };
        var html = $"""
            <!doctype html>
            <html lang="en">
            <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"></head>
            <body style="margin:0;background:#eef3f8;color:#203247;font-family:Arial,sans-serif;line-height:1.5">
            <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#eef3f8">
              <tr><td align="center" style="padding:24px 12px">
                <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="max-width:640px;background:#fff;border:1px solid #dce5ed;border-radius:12px">
                  <tr><td style="padding:16px 24px;background:#f7c948;color:#382600;border-radius:12px 12px 0 0;font-size:17px;font-weight:bold">DMS Lockbox · Approval reminder</td></tr>
                  <tr><td style="padding:24px;text-align:left">
                    <span style="display:inline-block;padding:3px 10px;background:{badgeBackground};color:#203247;border-radius:6px;font-size:13px;font-weight:bold">Risk: {summary.Risk}</span>
                    <h1 style="margin:12px 0 6px;font-size:23px;overflow-wrap:anywhere">{WebUtility.HtmlEncode(operation)}</h1>
                    <p style="margin:0 0 16px;color:#526478">This Lockbox request has been pending for at least 15 minutes.</p>
                    <p style="margin:0 0 20px"><a href="{WebUtility.HtmlEncode(detail.AbsoluteUri)}" style="display:inline-block;padding:9px 16px;background:#ffda69;color:#382600;border-radius:6px;text-decoration:none;font-weight:bold">Open in DMS</a></p>
                    <h2 style="margin:0 0 8px;font-size:16px">Request details</h2>
                    <table width="100%" cellspacing="0" cellpadding="0" style="border:1px solid #dce5ed;background:#f5f8fa;border-radius:8px;font-size:14px;overflow-wrap:anywhere">
                      {DetailRow("Request", summary.Id.ToString())}
                      {DetailRow("Requester", requester)}
                      {DetailRow("Submitted", submitted)}
                      {DetailRow("Expires", expires)}
                      {DetailRow("Target scope", targets)}
                    </table>
                    <h2 style="margin:18px 0 4px;font-size:16px">DMS CLI</h2>
                    <p style="margin:0 0 8px;color:#526478;font-size:13px">Sign in to DMS to review the full request before running either command.</p>
                    <p style="margin:0 0 8px">To approve, run:<br><code style="overflow-wrap:anywhere;word-break:break-word">{WebUtility.HtmlEncode(approve)}</code></p>
                    <p style="margin:0">To deny, run:<br><code style="overflow-wrap:anywhere;word-break:break-word">{WebUtility.HtmlEncode(deny)}</code></p>
                  </td></tr>
                </table>
              </td></tr>
            </table>
            </body>
            </html>
            """;
        return ($"[DMS Lockbox] [{summary.Risk}] {operation} - Approval required", text, html);
    }

    private static string DetailRow(string label, string value) =>
        $"""<tr><th scope="row" align="left" valign="top" style="padding:7px 12px;color:#526478;font-weight:normal">{label}</th><td valign="top" style="padding:7px 12px">{WebUtility.HtmlEncode(value)}</td></tr>""";

    private static string OneLine(string value) =>
        new(value.Select(character => char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character).ToArray());
}
