using System.Globalization;
using Aiursoft.DMS.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public enum OutputFormat { Table, Json, Csv, Log }

public static class DmsOutputWriter
{
    public static void Write(TextWriter output, DmsResponse response, OutputFormat format, bool callstack, bool color, int width = 120, bool catalog = false, bool audit = false, bool lockboxStatus = false, bool verbose = true, bool lockboxDetails = false, bool history = false, bool lockboxHistory = false)
    {
        if ((lockboxDetails || lockboxStatus) && response.Data is JObject lockbox)
            RemoveRetiredLockboxField(lockbox);
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
        if (history && data is JObject historyPage && historyPage["Entries"] is JArray historyEvents)
        {
            WriteHistory(output, historyPage, historyEvents, format, color, width, lockboxHistory);
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
        else WriteHuman(output, data, width, color, true, verbose);
    }

    private static void WriteHistory(TextWriter output, JObject page, JArray events, OutputFormat format, bool color, int width, bool lockboxHistory)
    {
        var flat = new JArray(events.OfType<JObject>().Select(item => new JObject(
            new JProperty("EventId", item["EventId"]?.DeepClone()), new JProperty("ControllerSequence", item["ControllerSequence"]?.DeepClone()),
            (item["Fact"] as JObject)?.Properties().Select(p => new JProperty(p.Name, p.Value.DeepClone())) ?? [],
            new JProperty("RequesterName", item["RequesterName"]?.DeepClone()),
            new JProperty("ApproverName", item["ApproverName"]?.DeepClone()),
            new JProperty("MachineName", item["MachineName"]?.DeepClone()))));
        if (format == OutputFormat.Csv)
        {
            WriteCsv(output, flat, new[] { "EventId", "ControllerSequence" }
                .Concat(typeof(WorkflowAuditEntry).GetProperties().Select(p => p.Name))
                .Concat(["RequesterName", "ApproverName", "MachineName"]));
            return;
        }
        output.WriteLine($"Archive window: {page["Echo"]?["From"]} to {page["Echo"]?["Until"]} (exclusive); page {page["Echo"]?["Page"]}; more: {page["HasNextPage"]}.");
        output.WriteLine("Archived events; recent changes may still be pending delivery. State is the event state, not a current snapshot.");
        string Name(JObject row, string idField, string nameField) => TerminalText(Cell(row[nameField] is { Type: not JTokenType.Null } ? row[nameField] : row[idField]));
        var rows = flat.OfType<JObject>().Select(e => new[]
        {
            e["RecordedAt"]!.Value<DateTime>().ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ((WorkflowAuditKind)e["Kind"]!.Value<int>()).ToString(),
            Cell((WorkflowAuditKind)e["Kind"]!.Value<int>() is WorkflowAuditKind.WorkflowCreated or WorkflowAuditKind.WorkflowStateChanged
                ? e["Definition"] : e["RequestedDefinition"] ?? e["Definition"]),
            Name(e, "RequesterId", "RequesterName"), Name(e, "ApproverId", "ApproverName"), Name(e, "MachineId", "MachineName"),
            HumanScalar("Decision", e["Decision"], true), HumanScalar("State", e["State"], true),
            HumanScalar("Origin", e["Origin"], true), Cell(e["LockboxId"]), Cell(e["WorkflowId"])
        }).ToList();
        if (rows.Count == 0) output.WriteLine("(empty)");
        else if (lockboxHistory)
            WriteRows(output, ["RecordedAt (UTC)", "Kind", "Operation", "Requester", "Machine", "Decision", "Approver", "LockboxId", "WorkflowId"],
                rows.Select(r => new[] { r[0], r[1], r[2], r[3], r[5], r[6], r[4], r[9], r[10] }).ToList(), width, color,
                [19, 16, Math.Max(12, width - 176), 18, 16, 12, 18, 39, 36]);
        else
            WriteRows(output, ["RecordedAt (UTC)", "Kind", "Operation", "State", "Requester", "Machine", "Origin", "LockboxId", "WorkflowId"],
                rows.Select(r => new[] { r[0], r[1], r[2], r[7], r[3], r[5], r[8], r[9], r[10] }).ToList(), width, color,
                [19, 20, Math.Max(12, width - 174), 17, 18, 16, 9, 39, 36]);
    }

    private static void RemoveRetiredLockboxField(JObject lockbox)
    {
        // Older Controllers can still send the retired field during a rolling upgrade.
        const string field = "LegacyApprovalWorkflowId";
        lockbox.Remove(field);
        (lockbox["Details"] as JObject)?.Remove(field);
        if (lockbox["Entries"] is JArray entries)
            foreach (var entry in entries.OfType<JObject>()) entry.Remove(field);
    }

    internal static void WriteWorkflowStatus(TextWriter output, JObject selected, JArray descendants, bool color, int width)
    {
        var details = (JObject)selected.DeepClone();
        if (!HasWorkflowResult(details["Result"])) details.Remove("Result");
        WriteHuman(output, details, width, color, workflowMetadata: true);

        if (descendants.Count == 0) return;
        output.WriteLine();
        output.WriteLine("Child workflows and results");
        output.WriteLine("---------------------------");
        foreach (var node in descendants.OfType<JObject>())
        {
            var definition = TerminalText(Cell(node["Definition"]));
            var machine = node["MachineName"]?.Value<string>() ?? node["MachineId"]?.ToString();
            var label = string.IsNullOrWhiteSpace(machine) ? definition : $"{TerminalText(machine)} · {definition}";
            var state = node["State"]!.ToObject<RunState>();
            output.WriteLine();
            output.WriteLine($"{label} {WorkflowTreeRenderer.Colorize($"[{state}]", state, color)}");
            output.WriteLine($"Workflow ID {Cell(node["Id"])}");
            if (HasWorkflowResult(node["Result"]))
                WriteHuman(output, node["Result"]!, width, color);
            else
                output.WriteLine("No result recorded.");
        }
    }

    private static bool HasWorkflowResult(JToken? result) => result?.Type switch
    {
        null or JTokenType.Null => false,
        JTokenType.String => !string.IsNullOrWhiteSpace(result.Value<string>()),
        _ => true
    };

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

    public static void WriteFilesystemResult(TextWriter output, TextWriter error, FilesystemListResult result,
        OutputFormat format, bool color, int width)
    {
        if (format == OutputFormat.Json)
        {
            WriteResult(output, result, format, color, width);
            return;
        }
        if (format == OutputFormat.Csv)
        {
            var rows = from machine in result.Machines
                       from entry in machine.Entries ?? []
                       select new
                       {
                           machine.Machine, machine.MachineId, machine.WorkflowId, machine.State, machine.Complete,
                           entry.Path, entry.Bytes, entry.Directory, entry.SymbolicLink, entry.LinkTarget,
                           entry.LastWriteTimeUtc, entry.CreationTimeUtc, entry.Mode
                       };
            WriteCsv(output, JArray.FromObject(rows), ["Machine", "MachineId", "WorkflowId", "State", "Complete",
                "Path", "Bytes", "Directory", "SymbolicLink", "LinkTarget", "LastWriteTimeUtc", "CreationTimeUtc", "Mode"]);
            if (result.State != RunState.Succeeded || result.Machines.Count == 0)
                error.WriteLine($"{result.WorkflowId}: {result.State} ({result.Machines.Count} machine results).");
        }
        else
            output.WriteLine($"{TerminalText(result.Path)} [{result.State}]");

        foreach (var machine in result.Machines)
        {
            var name = TerminalText(machine.Machine);
            var summary = machine.Entries switch
            {
                null when machine.State is RunState.Succeeded or RunState.Warning => "directory entries unavailable",
                null => "no directory result",
                { Count: 0 } => "empty directory",
                _ when machine.Complete == false => $"{machine.Entries.Count} entries, listing incomplete",
                _ => $"{machine.Entries.Count} entries"
            };
            if (format == OutputFormat.Csv)
            {
                if (machine.Entries is null or { Count: 0 } || machine.Complete == false || machine.State != RunState.Succeeded)
                    error.WriteLine($"{name} [{machine.State}] {summary}.");
                continue;
            }
            output.WriteLine();
            output.WriteLine($"{name} [{machine.State}] {summary}");
            if (machine.Entries is not { Count: > 0 } entries) continue;
            var rows = entries.Select(entry => new[]
            {
                entry.SymbolicLink ? "link" : entry.Directory ? "dir" : "file",
                entry.Bytes?.ToString(CultureInfo.InvariantCulture) ?? "-",
                entry.LinkTarget == null ? TerminalText(entry.Path) : $"{TerminalText(entry.Path)} -> {TerminalText(entry.LinkTarget)}"
            }).ToList();
            WriteRows(output, ["Type", "Bytes", "Path"], rows, width, color, [4, 20, width - 28]);
        }
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

    internal static void WriteEmptyTraceHint(TextWriter error, JObject page, Guid workflowId)
    {
        if (page["Entries"] is not JArray { Count: 0 }) return;

        error.WriteLine("No archived trace entries on this page.");
        if (page["PendingArchiveEvents"]?.Value<long>() is > 0)
            error.WriteLine("Some Controller events are still waiting to be archived.");
        error.WriteLine("Check execution status and available results with this command.");
        error.WriteLine($"  dms workflow status {workflowId}");
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
        var details = summary["Details"] as JObject;
        if (details != null)
        {
            rows.Add(["Workflow definition", TerminalText(Cell(details["Definition"]))]);
            rows.Add(["Workflow version", TerminalText(Cell(details["Version"]))]);
            rows.Add(["Risk", HumanScalar("Risk", details["Risk"], true)]);
            var targets = Structured(details["TargetMachines"] ?? JValue.CreateNull()) as JArray;
            rows.Add(["Frozen target machines", targets is not { Count: > 0 } ? "None (Controller workflow)" :
                string.Join('\n', targets.Select(id =>
                    $"{TerminalText(summary["MachineNames"]?[Cell(id)]?.Value<string>() ?? "Unknown machine")} ({TerminalText(Cell(id))})"))]);
        }
        else if (summary["Definition"] != null)
            rows.Add(["Workflow definition", TerminalText(Cell(summary["Definition"]))]);
        if (decision != DecisionState.Pending)
        {
            var verb = decision == DecisionState.Denied ? "Rejected" : decision.ToString();
            rows.Add([$"{verb} at", HumanScalar("DecidedAt", summary["DecidedAt"])]);
            rows.Add([$"{verb} by", summary["Automatic"]?.Value<bool>() == true
                ? "System (automatic approval)" : TerminalText(HumanScalar("DecidedBy", summary["DecidedBy"]))]);
            if (details != null)
                rows.Add(["Decision reason", TerminalText(Cell(details["DecisionReason"]))]);
        }
        rows.Add(["Eligible approvers now",
            approvers.Length == 0 ? "None" : string.Join('\n', approvers.Select(TerminalText))]);
        if (summary["WorkflowId"]?.Type is JTokenType.String or JTokenType.Guid)
            rows.Add(["Workflow ID", Cell(summary["WorkflowId"])]);
        WriteRows(output, ["Property", "Value"], rows, width, false);

        if (details != null)
        {
            output.WriteLine();
            output.WriteLine("Authoritative execution contract: workflow, version, risk, target IDs and inputs are frozen.");
            output.WriteLine("Request reason (unverified requester statement)");
            output.WriteLine(TerminalText(Cell(details["Reason"])));
            output.WriteLine("Verify the reason against the frozen execution contract before approving.");
            output.WriteLine();
            output.WriteLine("Frozen input parameters (JSON values)");
            var request = Structured(details["Parameters"] ?? JValue.CreateNull());
            var storedInput = (request as JObject)?["Input"];
            var input = storedInput != null ? Structured(storedInput) : null;
            if (input is JObject parameters && parameters.Count > 0)
                WriteRows(output, ["Parameter", "JSON value"], parameters.Properties().Select(p =>
                    new[] { TerminalText(p.Name), TerminalText(p.Value.ToString(Formatting.None)) }).ToList(), width, false,
                    [Math.Min(28, Math.Max(12, width / 3)), Math.Max(20, width - 30)]);
            else
                output.WriteLine(input?.ToString(Formatting.Indented) ?? "Unavailable; review the raw request below.");
            output.WriteLine();
            output.WriteLine("Raw input JSON");
            output.WriteLine(storedInput != null ? TerminalText(Cell(storedInput)) : "Unavailable");
            output.WriteLine();
            WriteRows(output, ["Property", "Value"], new List<string[]>
            {
                new[] { "Client request ID", TerminalText(Cell(details["ClientRequestId"])) },
                new[] { "Requester ID", TerminalText(Cell(details["RequesterId"])) },
                new[] { "Approver ID", TerminalText(Cell(details["ApproverId"])) }
            }, width, false);
            output.WriteLine();
            output.WriteLine("Stored request JSON (POST /api/v1/dms/lockboxes)");
            output.WriteLine(TerminalText(Cell(details["Parameters"])));
        }

        if (summary["EquivalentCommand"] is JValue { Value: string command } &&
            command.Length > 0 && !command.Any(char.IsControl))
        {
            output.WriteLine();
            output.WriteLine("Equivalent DMS command");
            output.WriteLine(command);
        }
    }

    internal static string TerminalText(string? text) => text == null ? "-" :
        new string(text.Select(character => char.IsControl(character) ? ' ' : character).ToArray());

    private static void WriteHuman(TextWriter output, JToken value, int width, bool color, bool workflowMetadata = false, bool verbose = true)
    {
        value = Structured(value);
        if (value is JObject obj)
        {
            if (obj.Count == 0) { output.WriteLine("(empty)"); return; }
            var properties = obj.Properties().Select(p => (p.Name, Value: Structured(p.Value))).ToList();
            var scalars = properties.Where(p => p.Name != "Result" &&
                                                p.Value is not (JObject or JArray) &&
                                                (verbose || p.Name is not ("Parameters" or "TargetMachines"))).ToList();
            WriteRows(output, ["Property", "Value"], scalars.Select(p => new[] { p.Name, HumanScalar(p.Name, p.Value, workflowMetadata) }).ToList(), width, color && workflowMetadata);
            foreach (var p in properties.Where(p => (p.Name == "Result" || p.Value is JObject or JArray) &&
                                                   (verbose || p.Name is not ("Parameters" or "TargetMachines"))))
            {
                output.WriteLine(); output.WriteLine(p.Name); output.WriteLine(new string('-', p.Name.Length));
                WriteHuman(output, p.Value, width, color, workflowMetadata && p.Name is "Definitions" or "RiskPermissions" or "Entries", verbose);
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
                    foreach (var item in array) { WriteHuman(output, item, width, color, workflowMetadata, verbose); output.WriteLine(); }
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
