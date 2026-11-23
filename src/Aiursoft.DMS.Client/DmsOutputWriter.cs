using System.Globalization;
using Aiursoft.DMS.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public enum OutputFormat { Table, Json, Csv, Log }

public static class DmsOutputWriter
{
    public static void Write(TextWriter output, DmsResponse response, OutputFormat format, bool callstack, bool color, int width = 120, bool catalog = false, bool audit = false, bool lockboxStatus = false)
    {
        if (format == OutputFormat.Json)
        {
            output.WriteLine(JsonConvert.SerializeObject(response, Formatting.Indented));
            return;
        }
        var data = response.Data as JToken ?? (response.Data == null ? JValue.CreateNull() : JToken.FromObject(response.Data));
        if (format == OutputFormat.Log)
        {
            WriteTraceLog(output, data);
            return;
        }
        if (audit && data is JObject page && page["Entries"] is JArray events)
        {
            var flat = new JArray(events.OfType<JObject>().Select(item => new JObject(
                new JProperty("EventId", item["EventId"]?.DeepClone()), new JProperty("ControllerSequence", item["ControllerSequence"]?.DeepClone()),
                (item["Fact"] as JObject)?.Properties().Select(p => new JProperty(p.Name, p.Value.DeepClone())) ?? [])));
            if (format == OutputFormat.Csv) WriteCsv(output, flat, new[] { "EventId", "ControllerSequence" }.Concat(typeof(WorkflowAuditEntry).GetProperties().Select(p => p.Name)));
            else
            {
                output.WriteLine($"Archive window: {page["Query"]?["From"]} to {page["Query"]?["Until"]} (exclusive); page {page["Query"]?["Page"]}; more: {page["HasNextPage"]}.");
                output.WriteLine("Archived events; recent changes may still be pending delivery. State is the event state, not a current snapshot.");
                var rows = flat.OfType<JObject>().Select(e => new[]
                {
                    e["RecordedAt"]!.Value<DateTime>().ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    ((WorkflowAuditKind)e["Kind"]!.Value<int>()).ToString(),
                    Cell((WorkflowAuditKind)e["Kind"]!.Value<int>() is WorkflowAuditKind.WorkflowCreated or WorkflowAuditKind.WorkflowStateChanged
                        ? e["Definition"] : e["RequestedDefinition"] ?? e["Definition"]),
                    HumanScalar("State", e["State"], true), HumanScalar("Origin", e["Origin"], true), Cell(e["LockboxId"]), Cell(e["WorkflowId"])
                }).ToList();
                if (rows.Count == 0) output.WriteLine("(empty)");
                else WriteRows(output, ["RecordedAt (UTC)", "Kind", "Operation", "State", "Origin", "LockboxId", "WorkflowId"], rows, width, color,
                    [19, 20, Math.Max(12, width - 152), 17, 9, 39, 36]);
            }
            return;
        }
        if (format == OutputFormat.Csv)
        {
            if (catalog && data is JObject details && details["Definitions"] is JArray definitions)
                data = new JArray(definitions.OfType<JObject>().Select(item => new JObject(
                    new JProperty("CanApproveOwnRequests", details["CanApproveOwnRequests"]?.DeepClone()),
                    item.Properties().Select(p => new JProperty(p.Name, p.Value.DeepClone())))));
            if (data is JObject identity && identity["RiskPermissions"] is JArray risks)
                data = new JArray(risks.OfType<JObject>().Select(item => new JObject(
                    identity.Properties().Where(p => p.Name != "RiskPermissions").Select(p => new JProperty(p.Name, p.Value.DeepClone())),
                    item.Properties().Select(p => new JProperty(p.Name, p.Value.DeepClone())))));
            WriteCsv(output, data);
        }
        else if (lockboxStatus && data is JObject summary)
            WriteLockboxStatus(output, summary, width);
        else if (callstack && data is JArray nodes)
            foreach (var line in WorkflowTreeRenderer.Render(nodes, color)) output.WriteLine(line);
        else WriteHuman(output, data, width, color, true);
    }

    public static void WriteResult(TextWriter output, object result, OutputFormat format, bool color, int width)
    {
        var data = JToken.FromObject(result);
        if (format == OutputFormat.Json)
            output.WriteLine(data.ToString(Formatting.Indented));
        else if (format == OutputFormat.Csv)
        {
            var emptyListType = data is JArray { Count: 0 }
                ? result.GetType().GetInterfaces().FirstOrDefault(type =>
                    type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                : null;
            var headers = emptyListType?.GetGenericArguments()[0].GetProperties().Select(property => property.Name);
            WriteCsv(output, data, headers);
        }
        else WriteHuman(output, data, width, color);
    }

    private static void WriteTraceLog(TextWriter output, JToken data)
    {
        if (data is not JObject page || page["Entries"] is not JArray entries)
            throw new ArgumentException("The Controller returned an invalid trace page.");

        foreach (var entry in entries.OfType<JObject>())
        {
            // Archived events store the source instead of a separate severity.
            var level = Cell(entry["Source"]).ToLowerInvariant() switch
            {
                "trace" => "TRACE",
                "debug" => "DEBUG",
                "warn" or "warning" or "stderr" => "WARN",
                "error" => "ERROR",
                "critical" or "fatal" => "FATAL",
                _ => "INFO"
            };
            var message = TerminalText(Cell(entry["Message"]).ReplaceLineEndings("\\n"));
            output.WriteLine($"{Cell(entry["Time"])} [{level}] {message}");
        }
    }

    private static void WriteLockboxStatus(TextWriter output, JObject summary, int width)
    {
        var decision = (DecisionState)summary["Decision"]!.Value<int>();
        var approvers = summary["EligibleApprovers"]?.Values<string>().ToArray() ?? [];
        List<string[]> rows =
        [
            ["Lockbox ID", Cell(summary["LockboxId"])],
            ["Requested by", TerminalText(Cell(summary["RequestedBy"]))],
            ["Requested at", HumanScalar("RequestedAt", summary["RequestedAt"])],
            ["Status", decision switch
            {
                DecisionState.Pending => "Awaiting approval",
                DecisionState.Denied => "Rejected",
                _ => decision.ToString()
            }]
        ];
        if (decision != DecisionState.Pending)
        {
            var verb = decision == DecisionState.Denied ? "Rejected" : decision.ToString();
            rows.Add([$"{verb} at", HumanScalar("DecidedAt", summary["DecidedAt"])]);
            rows.Add([$"{verb} by", summary["Automatic"]?.Value<bool>() == true
                ? "System (automatic approval)" : TerminalText(HumanScalar("DecidedBy", summary["DecidedBy"]))]);
        }
        rows.Add(["Eligible approvers now",
            approvers.Length == 0 ? "None" : string.Join('\n', approvers.Select(TerminalText))]);
        if (summary["WorkflowId"]?.Type is JTokenType.String or JTokenType.Guid)
            rows.Add(["Workflow ID", Cell(summary["WorkflowId"])]);
        WriteRows(output, ["Property", "Value"], rows, width, false);

        if (summary["EquivalentCommand"] is JValue { Value: string command } &&
            command.Length > 0 && !command.Any(char.IsControl))
        {
            output.WriteLine();
            output.WriteLine("Equivalent DMS command");
            output.WriteLine(command);
        }
    }

    private static string TerminalText(string? text) => text == null ? "-" :
        new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static void WriteHuman(TextWriter output, JToken value, int width, bool color, bool workflowMetadata = false)
    {
        value = Structured(value);
        if (value is JObject obj)
        {
            if (obj.Count == 0) { output.WriteLine("(empty)"); return; }
            var properties = obj.Properties().Select(p => (p.Name, Value: Structured(p.Value))).ToList();
            var scalars = properties.Where(p => p.Name != "Result" && p.Value is not (JObject or JArray)).ToList();
            WriteRows(output, ["Property", "Value"], scalars.Select(p => new[] { p.Name, HumanScalar(p.Name, p.Value, workflowMetadata) }).ToList(), width, color && workflowMetadata);
            foreach (var p in properties.Where(p => p.Name == "Result" || p.Value is JObject or JArray))
            {
                output.WriteLine(); output.WriteLine(p.Name); output.WriteLine(new string('-', p.Name.Length));
                WriteHuman(output, p.Value, width, color, workflowMetadata && p.Name is "Definitions" or "RiskPermissions" or "Entries");
            }
        }
        else if (value is JArray array)
        {
            if (array.Count == 0) { output.WriteLine("(empty)"); return; }
            if (array.All(v => v is JObject))
            {
                var headers = array.Cast<JObject>().SelectMany(o => o.Properties().Select(p => p.Name)).Distinct().ToArray();
                var rows = array.Cast<JObject>().Select(o => headers.Select(h => HumanScalar(h, o[h], workflowMetadata)).ToArray()).ToList();
                if (headers.Length > 0 && Widths(headers, rows).Sum() + (headers.Length - 1) * 2 <= width)
                    WriteRows(output, headers, rows, width, color && workflowMetadata);
                else
                    foreach (var item in array) { WriteHuman(output, item, width, color, workflowMetadata); output.WriteLine(); }
            }
            else foreach (var item in array) WriteHuman(output, item, width, color);
        }
        else output.WriteLine(HumanScalar(string.Empty, value));
    }

    private static JToken Structured(JToken value)
    {
        if (value.Type != JTokenType.String) return value;
        var text = value.Value<string>()!.TrimStart();
        if (!text.StartsWith('{') && !text.StartsWith('[')) return value;
        try { return JToken.Parse(text); }
        catch (JsonReaderException) { return value; }
    }

    private static string HumanScalar(string name, JToken? value, bool workflowMetadata = false)
    {
        if (value == null || value.Type == JTokenType.Null) return "-";
        if (workflowMetadata && value.Type == JTokenType.Integer && name is "State" or "Decision" or "Risk" or "Location" or "Origin" or "Trigger")
        {
            var number = value.Value<int>();
            if (name == "Origin" && Enum.IsDefined((WorkflowOrigin)number)) return ((WorkflowOrigin)number).ToString();
            if (name == "Trigger" && Enum.IsDefined((WorkflowTrigger)number)) return ((WorkflowTrigger)number).ToString();
            if (name == "State" && Enum.IsDefined((RunState)number)) return ((RunState)number).ToString();
            if (name == "Decision" && Enum.IsDefined((DecisionState)number)) return ((DecisionState)number).ToString();
            if (name == "Risk" && Enum.IsDefined((RiskLevel)number)) return ((RiskLevel)number).ToString();
            if (name == "Location" && Enum.IsDefined((WorkflowLocation)number)) return ((WorkflowLocation)number).ToString();
        }
        if (value.Type == JTokenType.Date) return value.Value<DateTime>().ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return Cell(value);
    }

    private static int[] Widths(string[] headers, List<string[]> rows) => headers.Select((h, i) =>
        Math.Max(h.Length, rows.SelectMany(r => r[i].Replace("\r\n", "\n").Split('\n')).Select(s => s.Length).DefaultIfEmpty().Max())).ToArray();

    private static void WriteRows(TextWriter output, string[] headers, List<string[]> rows, int width, bool color, int[]? maxWidths = null)
    {
        if (rows.Count == 0) return;
        var widths = Widths(headers, rows);
        if (maxWidths != null) widths = widths.Select((value, i) => Math.Min(value, maxWidths[i])).ToArray();
        if (headers is ["Property", "Value"]) widths[1] = Math.Min(widths[1], Math.Max(36, width - widths[0] - 2));
        output.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        output.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            var lines = row.Select((cell, i) => cell.Replace("\r\n", "\n").Split('\n')
                .SelectMany(line => line.Length == 0 ? [string.Empty] : line.Chunk(Math.Max(1, widths[i])).Select(chars => new string(chars))).ToArray()).ToArray();
            for (var line = 0; line < lines.Max(c => c.Length); line++)
            {
                var cells = lines.Select((cell, i) => (line < cell.Length ? cell[line] : string.Empty).PadRight(widths[i])).ToArray();
                for (var i = 0; i < cells.Length; i++)
                {
                    var stateColumn = headers[i] == "State" || (i == 1 && row[0] == "State");
                    if (stateColumn && Enum.TryParse<RunState>(cells[i].Trim(), out var state))
                        cells[i] = WorkflowTreeRenderer.Colorize(cells[i], state, color);
                }
                output.WriteLine(string.Join("  ", cells).TrimEnd());
            }
        }
    }

    private static void WriteCsv(TextWriter output, JToken data, IEnumerable<string>? defaultHeaders = null)
    {
        string[] defaults = defaultHeaders?.ToArray() ?? [];
        if (data is JObject page && page["Entries"] is JArray entries)
        {
            data = entries;
            defaults = page["Page"] != null ? typeof(LockboxDetails).GetProperties().Select(p => p.Name).ToArray()
                : ["EventId", "WorkflowId", "WorkflowDefinition", "Time", "Source", "Message"];
        }
        var records = data is JArray array ? array.ToList() : [data];
        var objects = records.Select(r => r as JObject ?? new JObject { ["Value"] = r }).ToList();
        var headers = defaults.Concat(objects.SelectMany(o => o.Properties().Select(p => p.Name))).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (headers.Length == 0) return;
        output.Write(string.Join(',', headers.Select(EscapeCsv)) + "\r\n");
        foreach (var obj in objects) output.Write(string.Join(',', headers.Select(h => EscapeCsv(Cell(obj[h])))) + "\r\n");
    }

    private static string Cell(JToken? value) => value?.Type switch
    {
        null or JTokenType.Null => string.Empty,
        JTokenType.String => value.Value<string>()!,
        JTokenType.Guid => value.Value<Guid>().ToString(),
        JTokenType.Date => value.Value<DateTime>().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString(Formatting.None)
    };

    private static string EscapeCsv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? '"' + value.Replace("\"", "\"\"") + '"' : value;
}
