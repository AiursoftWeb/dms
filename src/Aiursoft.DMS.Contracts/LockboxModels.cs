using System.ComponentModel;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aiursoft.DMS.Contracts;

// Only public Lockbox identifiers carry a prefix. Database keys remain UUIDs.
[TypeConverter(typeof(LockboxIdConverter))]
[JsonConverter(typeof(LockboxIdJsonConverter))]
[Newtonsoft.Json.JsonConverter(typeof(LockboxIdNewtonsoftConverter))]
public readonly record struct LockboxId(Guid Value)
{
    public override string ToString() => $"lb/{Value:D}";
    public static implicit operator LockboxId(Guid value) => new(value);
    public static LockboxId Parse(string value) => TryParse(value, out var id) ? id
        : throw new FormatException("Expected a Lockbox ID in the form lb/<UUID>.");
    public static bool TryParse(string? value, out LockboxId id)
    {
        // Bare UUIDs remain readable in historical records and saved links.
        var text = value?.StartsWith("lb/", StringComparison.Ordinal) == true ? value[3..] : value;
        var valid = Guid.TryParseExact(text, "D", out var parsed);
        id = new LockboxId(parsed); return valid;
    }
}

public sealed class LockboxIdConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string text ? LockboxId.Parse(text) : base.ConvertFrom(context, culture, value);
}

public sealed class LockboxIdJsonConverter : JsonConverter<LockboxId>
{
    public override LockboxId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => LockboxId.Parse(reader.GetString()!);
    public override void Write(Utf8JsonWriter writer, LockboxId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

public sealed class LockboxIdNewtonsoftConverter : Newtonsoft.Json.JsonConverter<LockboxId>
{
    public override LockboxId ReadJson(Newtonsoft.Json.JsonReader reader, Type objectType, LockboxId existingValue, bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer) => LockboxId.Parse((string)reader.Value!);
    public override void WriteJson(Newtonsoft.Json.JsonWriter writer, LockboxId value, Newtonsoft.Json.JsonSerializer serializer) => writer.WriteValue(value.ToString());
}

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record LockboxReceipt(LockboxId LockboxId, Guid? WorkflowId);
public sealed record OpenWorkflowResult(Guid WorkflowId, RunState State, string? Result);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record LockboxDetails(
    LockboxId LockboxId, Guid ClientRequestId, string RequesterId, string Definition, string Version,
    RiskLevel Risk, string Parameters, string TargetMachines, string Reason, DateTime CreatedAt,
    DecisionState Decision, string? ApproverId, bool Automatic, DateTime? DecidedAt, string DecisionReason,
    Guid? WorkflowId, DateTime ExpiresAt);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record LockboxPage(IReadOnlyList<LockboxDetails> Entries, int Page, bool HasNextPage);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by clients outside the server solution.")]
public sealed record LockboxReviewEntry(LockboxId LockboxId, string Requester, string Definition, RiskLevel Risk, DateTime CreatedAt);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Page is part of the public paginated JSON response.")]
public sealed record LockboxReviewPage(IReadOnlyList<LockboxReviewEntry> Entries, int Page, bool HasNextPage);
public sealed record LockboxReviewUpdate(DateTime? LatestPendingAt);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by clients outside the server solution.")]
public sealed record LockboxHistoryEntry(LockboxId LockboxId, string Requester, string Definition, RiskLevel Risk,
    DecisionState Decision, DateTime DecidedAt);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Page is part of the public paginated JSON response.")]
public sealed record LockboxHistoryPage(IReadOnlyList<LockboxHistoryEntry> Entries, int Page, bool HasNextPage);

[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Public JSON response properties are consumed by API and SDK clients.")]
public sealed record LockboxStatus(
    LockboxId LockboxId, string RequestedBy, DateTime RequestedAt, DecisionState Decision,
    DateTime? DecidedAt, string? DecidedBy, bool Automatic, IReadOnlyList<string> EligibleApprovers, Guid? WorkflowId,
    DateTime ExpiresAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public LockboxDetails? Details { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? CanApprove { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [Newtonsoft.Json.JsonProperty(DefaultValueHandling = Newtonsoft.Json.DefaultValueHandling.Ignore)]
    public bool DefinitionChanged { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public IReadOnlyDictionary<Guid, string>? MachineNames { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? Definition { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public string? EquivalentCommand { get; init; }
}
