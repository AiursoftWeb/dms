using System.CommandLine;
using System.Text;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;

namespace Aiursoft.DMS.Client;

internal sealed class DatabaseUpsertCommand : DmsCommandHandler
{
    private readonly Option<Guid> _machine = new("--machine-id")
        { Description = "Target machine ID (required when submitting confidential secret columns)." };
    private readonly Option<string> _reason = new("--reason")
        { Required = true, Description = "Reason recorded in the Lockbox." };
    private readonly Option<string?> _connection = new("--connection")
        { Description = "Registered connection profile name from dms machine database discover (mutually exclusive with --container)." };
    private readonly Option<string?> _container = new("--container")
        { Description = "Local database container full 64-hex ID from dms machine database discover (mutually exclusive with --connection)." };
    private readonly Option<string> _database = new("--database")
        { Required = true, Description = "Target database name." };
    private readonly Option<string> _table = new("--table")
        { Required = true, Description = "Target table name." };
    private readonly Option<string[]> _match = new("--match")
        { Required = true, AllowMultipleArgumentsPerToken = true, Description = "Primary/unique key column name(s) used to match the existing row (repeatable)." };
    private readonly Option<string[]?> _set = new("--set")
        { AllowMultipleArgumentsPerToken = true, Description = "Non-secret string column assignment(s) in Column=Value form (repeatable)." };
    private readonly Option<string?> _valuesJson = new("--values-json")
        { Description = "Optional JSON object of typed non-secret column values (numbers, booleans, null, strings)." };
    private readonly Option<string[]?> _secretColumn = new("--secret-column")
        { AllowMultipleArgumentsPerToken = true, Description = "Sensitive column name(s) read privately and never stored in Lockbox/audit (repeatable)." };
    private readonly Option<bool> _secretStdin = new("--secret-stdin")
        { Description = "Read confidential secret value(s) from stdin (single line for one secret column, or JSON object for multiple)." };
    private readonly Option<int> _timeout = new("--timeout-seconds")
        { DefaultValueFactory = _ => 30, Description = "Maximum execution time in seconds (1 to 300)." };

    protected override string Name => "upsert";

    protected override string Description =>
        "Insert or update one row with structured columns; optional --secret-column values bypass Lockbox/audit logs via encrypted transfer.";

    protected override IEnumerable<Option> GetActionOptions()
    {
        yield return _machine;
        yield return _reason;
        yield return _connection;
        yield return _container;
        yield return _database;
        yield return _table;
        yield return _match;
        yield return _set;
        yield return _valuesJson;
        yield return _secretColumn;
        yield return _secretStdin;
        yield return _timeout;
    }

    protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
    {
        var machine = context.GetValue(_machine);
        var reason = context.GetValue(_reason)!;
        var connection = context.GetValue(_connection);
        var container = context.GetValue(_container);
        var database = context.GetValue(_database)!;
        var table = context.GetValue(_table)!;
        var match = context.GetValue(_match)!;
        var setPairs = context.GetValue(_set) ?? [];
        var valuesJson = context.GetValue(_valuesJson);
        var secretColumns = context.GetValue(_secretColumn);
        var secretStdin = context.GetValue(_secretStdin);
        var timeout = context.GetValue(_timeout);

        var values = DmsCommandApp.ParseDatabaseUpsertValues(setPairs, valuesJson);
        var upsert = new DatabaseUpsertInput(
            connection,
            database,
            table,
            match,
            values,
            secretColumns is { Length: > 0 } ? secretColumns : null,
            timeout,
            container);
        upsert.Validate();

        if (upsert.SecretColumns is not { Length: > 0 })
        {
            if (secretStdin)
                throw new ArgumentException("--secret-stdin requires at least one --secret-column.");
            return await client.SubmitAsync(
                OperationsWorkflows.DatabaseUpsert,
                upsert,
                reason,
                machine == Guid.Empty ? [] : [machine]);
        }

        if (machine == Guid.Empty)
            throw new ArgumentException("Specify --machine-id when submitting confidential --secret-column values.");

        var secrets = ReadSecrets(upsert.SecretColumns, secretStdin, context.InvocationConfiguration.Error);
        new DatabaseUpsertSecretsPayload(secrets).Validate(upsert.SecretColumns);
        return await client.SubmitDatabaseUpsertAsync(new DatabaseUpsertSubmission(
            machine, reason, OperationsWorkflows.DatabaseUpsert.Serialize(upsert), secrets));
    }

    private static Dictionary<string, string> ReadSecrets(string[] columns, bool stdin, TextWriter error)
    {
        Dictionary<string, string> secrets = new(StringComparer.Ordinal);
        if (stdin)
        {
            var raw = Console.In.ReadToEnd();
            if (columns.Length == 1 && !raw.TrimStart().StartsWith('{'))
            {
                var line = raw.TrimEnd('\r', '\n');
                secrets[columns[0]] = line;
                return secrets;
            }
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("For multiple --secret-column entries, provide a JSON object on stdin.");
            foreach (var col in columns)
            {
                if (!doc.RootElement.TryGetProperty(col, out var el) || el.ValueKind != JsonValueKind.String)
                    throw new ArgumentException($"Missing string value for secret column '{col}' in stdin JSON.");
                secrets[col] = el.GetString()!;
            }
            return secrets;
        }

        if (Console.IsInputRedirected)
            throw new ArgumentException("Use --secret-stdin to read confidential column values from redirected input.");

        foreach (var col in columns)
        {
            error.Write($"Secret value for column '{col}' (hidden): ");
            try
            {
                var sb = new StringBuilder();
                while (true)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter) break;
                    if (key.Key == ConsoleKey.Escape) throw new ArgumentException("Secret entry cancelled.");
                    if (key.Key == ConsoleKey.Backspace && sb.Length > 0) sb.Length--;
                    if (char.IsControl(key.KeyChar)) continue;
                    if (sb.Length >= 16384) throw new ArgumentException("The secret value exceeds the input limit.");
                    sb.Append(key.KeyChar);
                }
                secrets[col] = sb.ToString();
            }
            finally { error.WriteLine(); }
        }
        return secrets;
    }
}

public static partial class DmsCommandApp
{
    public static Dictionary<string, JsonElement> ParseDatabaseUpsertValues(string[] setPairs, string? valuesJson)
    {
        Dictionary<string, JsonElement> result = new(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(valuesJson))
        {
            using var doc = JsonDocument.Parse(valuesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("--values-json must be a JSON object mapping column names to scalar values.");
            foreach (var prop in doc.RootElement.EnumerateObject())
                result[prop.Name] = prop.Value.Clone();
        }

        foreach (var pair in setPairs)
        {
            var eq = pair.IndexOf('=');
            if (eq < 1)
                throw new ArgumentException($"Invalid --set entry '{pair}'. Use Column=Value.");
            var col = pair[..eq];
            var val = pair[(eq + 1)..];
            if (!result.TryAdd(col, JsonSerializer.SerializeToElement(val)))
                throw new ArgumentException($"Duplicate column '{col}' specified in --set / --values-json.");
        }

        return result;
    }
}
