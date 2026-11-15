using System.Text.RegularExpressions;
using Aiursoft.AiurProtocol.Models;

namespace Aiursoft.DMS.Contracts;

public sealed partial record AgentRelease(string Architecture, string Version, string ArchiveSha256, string AssemblySha256)
{
    public void Validate()
    {
        if (Architecture is not ("linux-x64" or "linux-arm64") || !System.Version.TryParse(Version, out _)
            || !ValidHash(ArchiveSha256) || !ValidHash(AssemblySha256))
            throw new ArgumentException("Invalid Agent release identity.");
    }
    private static bool ValidHash(string? value) => value != null && Hash().IsMatch(value);
    [GeneratedRegex(@"\A[a-f0-9]{64}\z")]
    private static partial Regex Hash();
}

public sealed record AgentUpgradeInput(List<AgentRelease> Releases)
{
    public void Validate()
    {
        if (Releases == null || Releases.Count != 2) throw new ArgumentException("Both Agent release architectures are required.");
        foreach (var release in Releases) ValidateRelease(release);
        if (Releases.Select(r => r.Architecture).Distinct().Count() != 2) throw new ArgumentException("Both Agent release architectures are required.");
    }
    private static void ValidateRelease(AgentRelease? release)
    {
        if (release == null) throw new ArgumentException("An Agent release cannot be null.");
        release.Validate();
    }
}
public sealed record AgentUpgradeResult(string PreviousVersion, string Version, string Architecture, string ArchiveSha256, Guid InstallationId);
public sealed class AgentReleasesResponse : AiurResponse
{
    public List<AgentRelease> Data { get; set; } = [];
}
