using System.Text;

namespace Aiursoft.DMS.Contracts;

public sealed record ClickHouseDatabaseInput(string Database)
{
    public void Validate() => OperationValidation.Identifier(Database);
}

public sealed record ClickHouseTableInput(string Database, string Table)
{
    public void Validate()
    {
        OperationValidation.Identifier(Database);
        OperationValidation.Identifier(Table);
    }
}

public sealed record ClickHouseSqlInput(ArtifactReference Artifact, string? ContainerId = null,
    int TimeoutSeconds = 30, int MaxRows = 100) : IAuditTextInput
{
    public const int MaximumSqlBytes = 128 * 1024;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Artifact);
        Artifact.Validate();
        if (Artifact.Kind != "file" || Artifact.Length is < 1 or > MaximumSqlBytes)
            throw new ArgumentException("Supply a UTF-8 SQL file no larger than 128 KiB.");
        if (ContainerId != null) new DockerContainerInput(ContainerId).Validate();
        if (TimeoutSeconds is < 1 or > 60 || MaxRows is < 1 or > 1000)
            throw new ArgumentException("Timeout must be 1 to 60 seconds and row limit must be 1 to 1000.");
    }
}

public static class ClickHouseWorkflows
{
    public const string SqlSubmission = "machine.clickhouse.sql";

    [WorkflowRisk(RiskLevel.Open)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> FindContainer =
        new("machine.clickhouse.find-container", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(FindContainer)),
            PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.Open)]
    public static readonly MachineWorkflowDefinition<EmptyInput, OperationResult> ListDatabases =
        new("machine.clickhouse.list-databases", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(ListDatabases)),
            PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.Open)]
    public static readonly MachineWorkflowDefinition<ClickHouseDatabaseInput, OperationResult> ListTables =
        new("machine.clickhouse.list-tables", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(ListTables)),
            ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.Open)]
    public static readonly MachineWorkflowDefinition<ClickHouseTableInput, OperationResult> DescribeTable =
        new("machine.clickhouse.describe-table", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(DescribeTable)),
            ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<ClickHouseSqlInput, OperationResult> Query =
        new("machine.clickhouse.query", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(Query)),
            ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };

    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<ClickHouseSqlInput, OperationResult> Execute =
        new("machine.clickhouse.execute", WorkflowMetadata.Risk(typeof(ClickHouseWorkflows), nameof(Execute)),
            ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };

    public static bool IsClickHouse(string definition) => definition.StartsWith("machine.clickhouse.", StringComparison.Ordinal);

    public static bool HasPrivateResult(string definition) =>
        definition == ListDatabases.Name || definition == ListTables.Name ||
        definition == DescribeTable.Name || definition == Query.Name;
}

public static class ClickHouseSql
{
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > ClickHouseSqlInput.MaximumSqlBytes)
            throw new ArgumentException("Supply a UTF-8 SQL statement no larger than 128 KiB.");
        if (bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) bytes = bytes[3..];
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    public static RiskLevel Classify(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || Encoding.UTF8.GetByteCount(sql) > ClickHouseSqlInput.MaximumSqlBytes ||
            sql.Contains('\0'))
            throw new ArgumentException("Supply a nonempty UTF-8 SQL statement no larger than 128 KiB.");
        var tokens = ReadOnlySql.Tokens(sql);
        if (tokens.Count > 0 && Symbol(tokens[^1], ";")) tokens.RemoveAt(tokens.Count - 1);
        if (tokens.Count == 0 || tokens.Any(token => Symbol(token, ";")))
            throw new ArgumentException("Submit exactly one ClickHouse SQL statement.");

        var head = Keyword(tokens[0]);
        if (head is "SELECT" or "WITH" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN")
        {
            ReadOnlySql.Validate(sql);
            return RiskLevel.Low;
        }

        if (tokens.Any(t => Keyword(t) is "FUNCTION" or "OUTFILE" or "FORMAT" or "SETTINGS" or
            "GLOBAL" or "BACKUP" or "RESTORE" or "SYSTEM" or "GRANT" or "REVOKE" ||
            t.Kind == 0 && t.Value is "{" or "}"))
            throw new ArgumentException("This ClickHouse statement exceeds the supported write scope.");
        for (var index = 1; index < tokens.Count; index++)
            if (Keyword(tokens[index - 1]) == "ON" && Keyword(tokens[index]) == "CLUSTER")
                throw new ArgumentException("Submit a change to the selected ClickHouse server only.");

        var target = head switch
        {
            "INSERT" when WordAt(tokens, 1) == "INTO" => 2,
            "DELETE" when WordAt(tokens, 1) == "FROM" => 2,
            "UPDATE" => 1,
            "ALTER" when WordAt(tokens, 1) == "TABLE" => 2,
            _ => -1
        };
        if (target < 0 || !IdentifierAt(tokens, target))
            throw new ArgumentException("Use SELECT, INSERT, UPDATE, DELETE, or a supported ALTER TABLE statement.");
        var next = target + 1;
        if (next + 1 < tokens.Count && Symbol(tokens[next], ".") && IdentifierAt(tokens, next + 1))
            next += 2;

        var supported = head switch
        {
            "INSERT" => InsertSource(tokens, next),
            "DELETE" => WordAt(tokens, next) == "WHERE",
            "UPDATE" => WordAt(tokens, next) == "SET",
            _ => SupportedAlter(tokens, next)
        };
        if (!supported) throw new ArgumentException("This ClickHouse statement exceeds the supported write scope.");
        return RiskLevel.High;
    }

    private static bool InsertSource(List<ReadOnlySql.Token> tokens, int next)
    {
        if (next < tokens.Count && Symbol(tokens[next], "("))
        {
            next++;
            while (IdentifierAt(tokens, next))
            {
                next++;
                if (next < tokens.Count && Symbol(tokens[next], ")"))
                    return WordAt(tokens, next + 1) is "VALUES" or "SELECT" or "WITH";
                if (next >= tokens.Count || !Symbol(tokens[next], ",")) break;
                next++;
            }
            return false;
        }
        return WordAt(tokens, next) is "VALUES" or "SELECT" or "WITH";
    }

    private static bool SupportedAlter(List<ReadOnlySql.Token> tokens, int next)
    {
        var action = WordAt(tokens, next);
        if (action == "DELETE" && WordAt(tokens, next + 1) != "WHERE") return false;
        if (action is not ("UPDATE" or "DELETE") && (action is not ("ADD" or "DROP" or "MODIFY" or "RENAME" or "CLEAR") ||
            WordAt(tokens, next + 1) != "COLUMN"))
            return false;

        // Permit one ALTER action. UPDATE may have a list of assignments before WHERE.
        var depth = 0;
        var beforeWhere = action == "UPDATE";
        for (var index = next + 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (Symbol(token, "(") || Symbol(token, "[")) depth++;
            else if (Symbol(token, ")") || Symbol(token, "]")) depth--;
            else if (depth == 0 && Keyword(token) == "WHERE") beforeWhere = false;
            else if (depth == 0 && Symbol(token, ",") &&
                (!beforeWhere || !IdentifierAt(tokens, index + 1) || index + 2 >= tokens.Count ||
                    !Symbol(tokens[index + 2], "="))) return false;
        }
        return action != "UPDATE" || !beforeWhere;
    }

    private static string WordAt(List<ReadOnlySql.Token> tokens, int index) =>
        index < tokens.Count ? Keyword(tokens[index]) : "";

    private static string Keyword(ReadOnlySql.Token token) => token.Kind == 1 ? token.Value.ToUpperInvariant() : "";
    private static bool Symbol(ReadOnlySql.Token token, string value) => token.Kind == 0 && token.Value == value;
    private static bool IdentifierAt(List<ReadOnlySql.Token> tokens, int index) =>
        index < tokens.Count && tokens[index].Kind is 1 or 2;
}
