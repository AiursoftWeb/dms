using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed class AgentReleaseStore(IWebHostEnvironment environment)
{
    public List<AgentRelease> Read()
    {
        var path = Path.Combine(environment.ContentRootPath, "agent-assets", "releases.json");
        if (!File.Exists(path)) throw new DmsException(503, "Agent release assets are unavailable.");
        var releases = JsonSerializer.Deserialize<List<AgentRelease>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Invalid Agent release metadata.");
        new AgentUpgradeInput(releases).Validate();
        return releases;
    }
    public void RequireCurrent(AgentUpgradeInput input)
    {
        if (!input.Releases.OrderBy(r => r.Architecture, StringComparer.Ordinal).SequenceEqual(Read().OrderBy(r => r.Architecture, StringComparer.Ordinal)))
            throw new DmsException(409, "The requested Agent release changed. Submit a new Lockbox request.");
    }
}
