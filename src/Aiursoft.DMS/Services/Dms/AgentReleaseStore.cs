using System.Security.Cryptography;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AgentReleaseStore(IWebHostEnvironment environment)
{
    internal const string ChangedMessage = "The requested Agent release changed. Submit a new Lockbox request.";
    internal const string UnavailableMessage = "Agent release assets are unavailable.";
    internal const string PreviewChangedMessage = "The current Agent package changed again. Review it before submitting.";
    internal const string OriginalAvailableMessage = "The original Agent package is available again. Review the original request.";

    public List<AgentRelease> Read()
    {
        var path = Path.Combine(environment.ContentRootPath, "agent-assets", "releases.json");
        if (!File.Exists(path)) throw new DmsException(503, UnavailableMessage);
        try
        {
            var releases = JsonSerializer.Deserialize<List<AgentRelease>>(File.ReadAllText(path))
                ?? throw new InvalidDataException("Invalid Agent release metadata.");
            new AgentUpgradeInput(releases).Validate();
            return releases;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException)
        {
            throw new DmsException(503, UnavailableMessage);
        }
    }
    public void RequireCurrent(AgentUpgradeInput input)
    {
        if (!Matches(input.Releases, Read()))
            throw new DmsException(409, ChangedMessage);
    }

    internal List<AgentRelease>? ReadAvailable()
    {
        try { return Read(); }
        catch (DmsException exception) when (exception.StatusCode == 503) { return null; }
    }

    internal static bool Matches(IEnumerable<AgentRelease> requested, IEnumerable<AgentRelease> current) =>
        Ordered(requested).SequenceEqual(Ordered(current));

    internal static string Identity(IEnumerable<AgentRelease> releases) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Ordered(releases))));

    private static IOrderedEnumerable<AgentRelease> Ordered(IEnumerable<AgentRelease> releases) =>
        releases.OrderBy(release => release.Architecture, StringComparer.Ordinal);
}
