using System.Net.Mail;

namespace Aiursoft.DMS.Services.Dms;

public sealed class LockboxEmailOptions
{
    public bool Enabled { get; set; }
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string SmtpUsername { get; set; } = "";
    public string SmtpPassword { get; set; } = "";
    public string From { get; set; } = "";
    public string ControllerOrigin { get; set; } = "";

    internal bool IsValid() => !Enabled ||
        (Uri.CheckHostName(SmtpHost) != UriHostNameType.Unknown && SmtpPort is > 0 and <= 65535 &&
         (string.IsNullOrWhiteSpace(SmtpUsername) == string.IsNullOrEmpty(SmtpPassword)) &&
         MailAddress.TryCreate(From, out _) && Uri.TryCreate(ControllerOrigin, UriKind.Absolute, out var origin) &&
         origin.Scheme == Uri.UriSchemeHttps && origin.Host.Length != 0 && origin.UserInfo.Length == 0 &&
         origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0);
}
