using System.CommandLine;
using System.Text;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.SDK;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static Group ClickHouseCommands()
    {
        var database = new Option<string>("--database") { Required = true, Description = "ClickHouse database name." };
        var tableDatabase = new Option<string>("--database") { Required = true, Description = "ClickHouse database name." };
        var table = new Option<string>("--table") { Required = true, Description = "ClickHouse table name." };
        return new Group("clickhouse", "Run operations in the unique local ClickHouse server container.",
        [
            ClickHouseCommand("find-container", "Show the ClickHouse container selected for this machine.",
                ClickHouseWorkflows.FindContainer, _ => new EmptyInput()),
            ClickHouseCommand("list-databases", "List visible ClickHouse databases.",
                ClickHouseWorkflows.ListDatabases, _ => new EmptyInput()),
            ClickHouseCommand("list-tables", "List visible tables in a ClickHouse database.",
                ClickHouseWorkflows.ListTables, p => new ClickHouseDatabaseInput(p.GetValue(database)!), database),
            ClickHouseCommand("describe-table", "Show the names and types of a ClickHouse table's columns.",
                ClickHouseWorkflows.DescribeTable, p => new ClickHouseTableInput(p.GetValue(tableDatabase)!, p.GetValue(table)!),
                tableDatabase, table),
            new ClickHouseSqlCommand(),
            new ActionCommand("result", "Read the result of your approved ClickHouse Lockbox.",
                "clickhouse-result", positionalOnly: true)
        ]);
    }

    private static ActionCommand ClickHouseCommand<TInput>(string name, string description,
        MachineWorkflowDefinition<TInput, OperationResult> definition, Func<ParseResult, TInput> input,
        params Option[] options) =>
        new(name, description, definition.Name, definition, p => definition.Serialize(input(p)), options,
            readResult: payload => ClickHouseOutput(definition.ReadResult(payload)));

    private static object ClickHouseOutput(OperationResult value) =>
        value.Facts is { } facts ? JToken.Parse(facts.GetRawText()) : value;

    private static LockboxReceipt ClickHouseReceipt(DmsResponse response) =>
        (response.Data as JObject)?.ToObject<LockboxReceipt>()
        ?? throw new ArgumentException("The Controller returned no ClickHouse Lockbox receipt.");

    private static async Task<(DmsResponse Reply, OperationResult Value)> ReadClickHouseResultAsync(
        DmsApiClient client, LockboxId lockboxId, TextWriter error)
    {
        var reply = await client.WaitForClickHouseResultAsync(lockboxId);
        var result = (reply.Data as JObject)?.ToObject<OpenWorkflowResult>()
            ?? throw new ArgumentException("The Controller returned no ClickHouse workflow result.");
        if (result.State is not (RunState.Succeeded or RunState.Warning))
        {
            string? details = null;
            if (!string.IsNullOrEmpty(result.Result))
                details = JObject.Parse(result.Result).Value<string>("Error");
            var message = result.State.Terminal() || result.State == RunState.NeedsVerification
                ? $"Workflow {result.WorkflowId} is {result.State}. {details}"
                : $"Still waiting for Workflow {result.WorkflowId}. Read it again with 'dms machine clickhouse result {lockboxId}'.";
            throw new ArgumentException(message);
        }
        var value = JsonSerializer.Deserialize<OperationResult>(result.Result ?? "{}")
            ?? throw new ArgumentException("The Controller returned an invalid ClickHouse result.");
        if (string.IsNullOrEmpty(value.Summary))
            throw new ArgumentException("The Controller returned an empty ClickHouse result.");
        if (!value.Complete || result.State == RunState.Warning)
            error.WriteLine($"Warning: {value.Summary}");
        return (reply, value);
    }

    private sealed class ClickHouseSqlCommand : DmsCommandHandler
    {
        protected override string Name => "sql";
        protected override string Description => "Submit one ClickHouse statement. Read queries are low risk; supported writes require high-risk approval.";

        private readonly Option<Guid?> _machine = new("--machine-id")
            { Description = "Target machine ID. Omit when exactly one machine is enabled." };
        private readonly Option<string?> _sql = new("--sql")
            { Description = "One SQL statement. Omit both source options to read redirected standard input." };
        private readonly Option<string?> _file = new("--sql-file")
            { Description = "Read one UTF-8 SQL statement from a local file." };
        private readonly Option<string?> _reason = new("--reason")
            { Description = "Reason recorded in the Lockbox. Required for writes." };
        private readonly Option<Guid?> _request = new("--request-id")
            { Description = "Reuse this ID only to retry the same submission." };
        private readonly Option<bool> _showLockboxId = new("--show-lockbox-id")
            { Description = "Print the Lockbox receipt immediately after submission." };
        private readonly Option<int> _timeout = new("--timeout-seconds")
            { Description = "Query time limit from 1 to 60 seconds.", DefaultValueFactory = _ => 30 };
        private readonly Option<int> _maxRows = new("--max-rows")
            { Description = "Maximum returned rows, from 1 to 1000.", DefaultValueFactory = _ => 100 };
        private bool _hasResult;

        protected override IEnumerable<Option> GetActionOptions() =>
            [_machine, _sql, _file, _reason, _request, _showLockboxId, _timeout, _maxRows];

        protected override async Task<DmsResponse> RequestAsync(DmsApiClient client, ParseResult context)
        {
            _hasResult = false;
            var sql = await ReadSqlAsync(context.GetValue(_sql), context.GetValue(_file));
            var write = ClickHouseSql.Classify(sql) == RiskLevel.High;
            var reason = context.GetValue(_reason);
            if (write && string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("Provide --reason for the high-risk ClickHouse Lockbox.");
            reason = string.IsNullOrWhiteSpace(reason) ? "Investigate ClickHouse on the selected machine." : reason;
            var machine = context.GetValue(_machine);
            var machines = machine.HasValue ? new[] { machine.Value } : [];
            var request = context.GetValue(_request) ?? Guid.NewGuid();
            var timeout = context.GetValue(_timeout);
            var maxRows = context.GetValue(_maxRows);
            if (timeout is < 1 or > 60 || maxRows is < 1 or > 1000)
                throw new ArgumentException("Timeout must be 1 to 60 seconds and row limit must be 1 to 1000.");
            if (request == Guid.Empty || machine == Guid.Empty || reason.Length > 2000)
                throw new ArgumentException("Use valid request and machine IDs and a reason of at most 2000 characters.");

            string? container = null;
            if (write)
            {
                var found = ClickHouseReceipt(await client.SubmitAsync(ClickHouseWorkflows.FindContainer,
                    new EmptyInput(), "Identify the destination for a ClickHouse write.", machines));
                var (_, result) = await ReadClickHouseResultAsync(client, found.LockboxId, context.InvocationConfiguration.Error);
                container = result.Facts?.GetProperty("Id").GetString()
                    ?? throw new ArgumentException("The Controller returned no ClickHouse container ID.");
            }

            var directory = Directory.CreateTempSubdirectory("dms-clickhouse-");
            try
            {
                var file = Path.Combine(directory.FullName, "statement.sql");
                await File.WriteAllTextAsync(file, sql, new UTF8Encoding(false, true));
                var artifact = await PrepareOperationTextAsync(client, file, ClickHouseSqlInput.MaximumSqlBytes, request);
                var input = new ClickHouseSqlInput(artifact, container, timeout, maxRows);
                var operation = ClickHouseWorkflows.Query.Request(input, reason, machines, requestId: request);
                operation.Workflow = ClickHouseWorkflows.SqlSubmission;
                var response = await client.SubmitAsync(operation);
                var receipt = ClickHouseReceipt(response);
                if (write || receipt.WorkflowId == null || context.GetValue(_showLockboxId)) return response;
                try
                {
                    var (reply, result) = await ReadClickHouseResultAsync(client, receipt.LockboxId, context.InvocationConfiguration.Error);
                    reply.Data = ClickHouseOutput(result);
                    _hasResult = true;
                    return reply;
                }
                catch
                {
                    context.InvocationConfiguration.Error.WriteLine($"ClickHouse Lockbox {receipt.LockboxId}. Request ID: {request}.");
                    throw;
                }
            }
            finally { directory.Delete(recursive: true); }
        }

        private static async Task<string> ReadSqlAsync(string? text, string? file)
        {
            if (text != null && file != null)
                throw new ArgumentException("Pass --sql or --sql-file once, or pipe SQL to standard input.");
            if (text != null) return text;
            if (file != null)
            {
                await using var stream = File.OpenRead(file);
                if (stream.Length > ClickHouseSqlInput.MaximumSqlBytes)
                    throw new ArgumentException("The ClickHouse SQL file exceeds 128 KiB.");
                var bytes = new byte[ClickHouseSqlInput.MaximumSqlBytes + 1];
                var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false);
                return ClickHouseSql.Decode(bytes.AsSpan(0, count));
            }
            if (!Console.IsInputRedirected)
                throw new ArgumentException("Pass --sql, --sql-file, or pipe SQL to standard input.");
            var chars = new char[ClickHouseSqlInput.MaximumSqlBytes + 1];
            var read = await Console.In.ReadBlockAsync(chars);
            return new string(chars, 0, read);
        }

        protected override void WriteResponse(ParseResult context, DmsResponse response, OutputFormat format, bool color, int width)
        {
            if (_hasResult) DmsOutputWriter.WriteResult(context.InvocationConfiguration.Output, response.Data!, format, color, width);
            else base.WriteResponse(context, response, format, color, width);
        }
    }
}
