using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public static class ClickHouseOperations
{
    private const int ResultCharacters = 20000;
    private const int MetadataRows = 1000;

    private sealed record Container(string Id, string Image, string Name);

    public static async Task<OperationResult> FindAsync(MachineWorkflowContext context, EmptyInput input,
        AgentEnvironment agent, CancellationToken token)
    {
        var container = await ResolveAsync(context, agent, token);
        return new OperationResult("One running ClickHouse container found.",
            Facts: JsonSerializer.SerializeToElement(new { container.Id, container.Image, container.Name }));
    }

    public static Task<OperationResult> ListDatabasesAsync(MachineWorkflowContext context, EmptyInput input,
        AgentEnvironment agent, CancellationToken token) =>
        ReadAsync(context, agent, "SHOW DATABASES", 30, MetadataRows, token);

    public static Task<OperationResult> ListTablesAsync(MachineWorkflowContext context, ClickHouseDatabaseInput input,
        AgentEnvironment agent, CancellationToken token) =>
        ReadAsync(context, agent, $"SHOW TABLES FROM `{input.Database}`", 30, MetadataRows, token);

    public static Task<OperationResult> DescribeTableAsync(MachineWorkflowContext context, ClickHouseTableInput input,
        AgentEnvironment agent, CancellationToken token) =>
        ReadAsync(context, agent, $"DESCRIBE TABLE `{input.Database}`.`{input.Table}`", 30, MetadataRows, token,
            columns: ["name", "type"]);

    public static async Task<OperationResult> QueryAsync(MachineWorkflowContext context, ClickHouseSqlInput input,
        AgentEnvironment agent, CancellationToken token)
    {
        var sql = await ReadSqlAsync(input, agent, RiskLevel.Low, token);
        return await ReadAsync(context, agent, sql, input.TimeoutSeconds, input.MaxRows, token);
    }

    public static async Task<OperationResult> ExecuteAsync(MachineWorkflowContext context, ClickHouseSqlInput input,
        AgentEnvironment agent, CancellationToken token)
    {
        var sql = await ReadSqlAsync(input, agent, RiskLevel.High, token);
        var container = await ResolveAsync(context, agent, token);
        if (container.Id != input.ContainerId)
            throw new InvalidOperationException("The ClickHouse container changed after submission. Submit a new Lockbox for the current container.");

        await RunClientAsync(context, agent, container.Id, sql, input.TimeoutSeconds, input.MaxRows, write: true, token);
        return new OperationResult("Approved ClickHouse statement completed.");
    }

    private static async Task<string> ReadSqlAsync(ClickHouseSqlInput input, AgentEnvironment agent, RiskLevel risk,
        CancellationToken token)
    {
        var sql = await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent,
            ClickHouseSqlInput.MaximumSqlBytes, token);
        if (ClickHouseSql.Classify(sql) != risk || (risk == RiskLevel.High) != (input.ContainerId != null))
            throw new ArgumentException("The frozen SQL and container must match the approved ClickHouse workflow.");
        return sql;
    }

    private static async Task<Container> ResolveAsync(MachineWorkflowContext context, AgentEnvironment agent,
        CancellationToken token)
    {
        var listed = await OperationOutput.CommandAsync(context, agent, "docker",
            DockerOperations.Arguments("ps", "--no-trunc", "--filter", "status=running", "--format", "{{.ID}}\t{{.Image}}\t{{.Names}}"),
            token, traceOutput: false, seconds: 10);
        var candidates = new List<Container>();
        foreach (var line in listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 3)
                throw new InvalidDataException("Docker returned invalid container metadata.");
            var image = fields[1][(fields[1].LastIndexOf('/') + 1)..].Split(':', '@')[0];
            if (image != "clickhouse-server") continue;
            new DockerContainerInput(fields[0]).Validate();
            candidates.Add(new Container(fields[0], fields[1], fields[2]));
        }
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count == 0)
            throw new InvalidOperationException("No running ClickHouse server container was found on the target machine.");
        var names = string.Join(", ", candidates.Take(8).Select(c => $"{c.Name} ({c.Id[..12]})"));
        if (candidates.Count > 8) names += ", …";
        throw new InvalidOperationException($"Found {candidates.Count} ClickHouse server containers. Exactly one is required. Matches: {names}.");
    }

    private static async Task<OperationResult> ReadAsync(MachineWorkflowContext context, AgentEnvironment agent,
        string sql, int seconds, int maxRows, CancellationToken token, string[]? columns = null)
    {
        var container = await ResolveAsync(context, agent, token);
        var native = await RunClientAsync(context, agent, container.Id, sql, seconds, maxRows, write: false, token);
        var rows = new List<JsonElement>();
        var size = 0;
        var complete = true;
        foreach (var line in native.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rows.Count == maxRows)
            {
                complete = false;
                break;
            }
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("ClickHouse returned a non-row response.");
            var row = columns == null ? document.RootElement.Clone() : JsonSerializer.SerializeToElement(
                columns.ToDictionary(column => column, column => document.RootElement.GetProperty(column)));
            size += JsonSerializer.Serialize(row).Length + 1;
            if (size > ResultCharacters)
            {
                complete = false;
                break;
            }
            rows.Add(row);
        }
        return new OperationResult(complete ? $"ClickHouse returned {rows.Count} rows." :
            $"ClickHouse output is incomplete at {rows.Count} rows. Narrow your query to see more results.",
            complete, Facts: JsonSerializer.SerializeToElement(rows));
    }

    private static async Task<NativeResult> RunClientAsync(MachineWorkflowContext context, AgentEnvironment agent,
        string container, string sql, int seconds, int maxRows, bool write, CancellationToken token)
    {
        List<string> args = ["exec", "-i", container, "clickhouse-client",
            "--host", "127.0.0.1", "--format", "JSONEachRow",
            $"--max_execution_time={seconds.ToString(CultureInfo.InvariantCulture)}"];
        if (write) args.Add("--mutations_sync=2");
        else
        {
            args.AddRange(["--readonly=1", "--allow_ddl=0", "--allow_introspection_functions=0",
                $"--max_result_rows={(maxRows + 1).ToString(CultureInfo.InvariantCulture)}",
                "--max_result_bytes=524288", "--result_overflow_mode=break",
                "--max_memory_usage=536870912", "--max_bytes_to_read=1073741824"]);
        }
        await context.TraceAsync($"Using the ClickHouse {(write ? "write" : "read-only")} client in Docker container {container}.");
        return await OperationOutput.CommandAsync(context, agent, "docker", DockerOperations.Arguments(args.ToArray()),
            token, change: write, seconds: seconds + 5, stdin: sql, traceOutput: false);
    }
}
