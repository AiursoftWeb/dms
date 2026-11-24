using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aiursoft.DMS.Contracts;

public sealed record SecurityBaselinePolicy(string Version, bool RequireFail2ban = false, bool? UfwEnabled = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutomaticallyManageSsh { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || Version.Length > 80 || Version.Any(char.IsControl))
            throw new ArgumentException("The security baseline requires a version of at most 80 characters.");
    }
}

public sealed record ScheduledSecurityBaselineSettings(Guid[] MachineIds, SecurityBaselinePolicy Policy)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public static ScheduledSecurityBaselineSettings? Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var settings = JsonSerializer.Deserialize<ScheduledSecurityBaselineSettings>(value, JsonOptions);
        if (settings == null || settings.MachineIds.Contains(Guid.Empty) ||
            settings.MachineIds.Distinct().Count() != settings.MachineIds.Length)
            throw new ArgumentException("Set a security baseline policy and provide distinct, nonempty machine IDs.");
        settings.Policy.Validate();
        return settings;
    }
}
