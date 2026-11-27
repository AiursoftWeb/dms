using System.Text;
using System.Text.Json;

namespace Aiursoft.DMS.Contracts;

public sealed record DatabaseUpsertInput(
    string? Connection,
    string Database,
    string Table,
    string[] MatchColumns,
    Dictionary<string, JsonElement> Values,
    string[]? SecretColumns = null,
    int TimeoutSeconds = 30,
    string? Container = null) : IOperationInput
{
    public void Validate()
    {
        OperationValidation.DatabaseTarget(Connection, Container);
        OperationValidation.Identifier(Database);
        OperationValidation.Identifier(Table);
        ArgumentNullException.ThrowIfNull(MatchColumns);
        ArgumentNullException.ThrowIfNull(Values);

        if (MatchColumns.Length is < 1 or > 20 ||
            MatchColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != MatchColumns.Length)
            throw new ArgumentException("Specify 1 to 20 unique match columns.");
        foreach (var col in MatchColumns) OperationValidation.Identifier(col);

        if (Values.Count is < 1 or > 100)
            throw new ArgumentException("Specify 1 to 100 non-secret column values.");

        var valueKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, val) in Values)
        {
            OperationValidation.Identifier(col);
            if (!valueKeys.Add(col))
                throw new ArgumentException($"Duplicate column '{col}' in Values.");
            if (val.ValueKind is not (JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
                    or JsonValueKind.True or JsonValueKind.False) ||
                val.GetRawText().Length > 65536)
                throw new ArgumentException($"Column '{col}' value must be a bounded scalar JSON value (max 64 KiB).");
        }

        foreach (var matchCol in MatchColumns)
        {
            if (!valueKeys.Contains(matchCol))
                throw new ArgumentException($"Match column '{matchCol}' must be included in Values.");
        }

        if (SecretColumns != null)
        {
            if (SecretColumns.Length > 20 ||
                SecretColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != SecretColumns.Length)
                throw new ArgumentException("Specify at most 20 unique secret columns.");
            foreach (var secretCol in SecretColumns)
            {
                OperationValidation.Identifier(secretCol);
                if (valueKeys.Contains(secretCol))
                    throw new ArgumentException($"Secret column '{secretCol}' must not also appear in Values.");
            }
        }

        if (TimeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Timeout must be 1 to 300 seconds.");
    }
}

public sealed record DatabaseUpsertSubmission(
    Guid MachineId,
    string Reason,
    string UpsertInputJson,
    Dictionary<string, string> Secrets)
{
    public override string ToString() => nameof(DatabaseUpsertSubmission);
}

public sealed record DatabaseUpsertSecretsPayload(Dictionary<string, string> Secrets)
{
    public override string ToString() => nameof(DatabaseUpsertSecretsPayload);

    public void Validate(IReadOnlyCollection<string> expectedColumns)
    {
        ArgumentNullException.ThrowIfNull(Secrets);
        if (Secrets.Count != expectedColumns.Count)
            throw new ArgumentException("Secret values must match the declared SecretColumns exactly.");
        foreach (var column in expectedColumns)
        {
            if (!Secrets.TryGetValue(column, out var value) ||
                string.IsNullOrEmpty(value) ||
                value.Contains('\0') ||
                Encoding.UTF8.GetByteCount(value) > 16384)
                throw new ArgumentException($"Secret value for column '{column}' must be nonempty UTF-8 text without NUL (max 16 KiB).");
        }
    }
}
