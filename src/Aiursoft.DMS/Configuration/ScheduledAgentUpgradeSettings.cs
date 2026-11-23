using System.Text.Json;

namespace Aiursoft.DMS.Configuration;

internal static class ScheduledAgentUpgradeSettings
{
    public static Guid[] Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var ids = JsonSerializer.Deserialize<Guid[]>(value);
        if (ids == null || ids.Contains(Guid.Empty) || ids.Distinct().Count() != ids.Length)
            throw new ArgumentException("Provide a JSON array of distinct, nonempty machine IDs.");
        return ids;
    }
}
