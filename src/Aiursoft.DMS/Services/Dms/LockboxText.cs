using System.Globalization;

namespace Aiursoft.DMS.Services.Dms;

public static class LockboxText
{
    public const string Name = "Lockbox";
    public const string MyRequests = "My {0} requests";

    public static string Format(string template) => string.Format(CultureInfo.CurrentCulture, template, Name);
}
