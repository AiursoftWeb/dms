using System.Globalization;
using Aiursoft.DMS.Contracts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Aiursoft.DMS.Client;

public enum OutputFormat { Table, Json, Csv }

public static class DmsOutputWriter
{
    public static void Write(TextWriter output, DmsResponse response, OutputFormat format, bool callstack, bool color, int width = 120)
    {
        if (format == OutputFormat.Json)
        {
            output.WriteLine(JsonConvert.SerializeObject(response, Formatting.Indented));
            return;
        }
        var data = response.Data as JToken ?? (response.Data == null ? JValue.CreateNull() : JToken.FromObject(response.Data));
        if (format == OutputFormat.Csv) WriteCsv(output, data);
        else if (callstack && data is JArray nodes)
            foreach (var line in WorkflowTreeRenderer.Render(nodes, color)) output.WriteLine(line);
        else WriteHuman(output, data, width, color, true);
    }

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
                WriteHuman(output, p.Value, width, color);
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
        if (workflowMetadata && value.Type == JTokenType.Integer && name is "State" or "Decision" or "Risk")
        {
            var number = value.Value<int>();
            if (name == "State" && Enum.IsDefined((RunState)number)) return ((RunState)number).ToString();
            if (name == "Decision" && Enum.IsDefined((DecisionState)number)) return ((DecisionState)number).ToString();
            if (name == "Risk" && Enum.IsDefined((RiskLevel)number)) return ((RiskLevel)number).ToString();
        }
        if (value.Type == JTokenType.Date) return value.Value<DateTime>().ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        return Cell(value);
    }

    private static int[] Widths(string[] headers, List<string[]> rows) => headers.Select((h, i) =>
        Math.Max(h.Length, rows.SelectMany(r => r[i].Replace("\r\n", "\n").Split('\n')).Select(s => s.Length).DefaultIfEmpty().Max())).ToArray();

    private static void WriteRows(TextWriter output, string[] headers, List<string[]> rows, int width, bool color)
    {
        if (rows.Count == 0) return;
        var widths = Widths(headers, rows);
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

    private static void WriteCsv(TextWriter output, JToken data)
    {
        string[] defaults = [];
        if (data is JObject page && page["Entries"] is JArray entries)
        {
            data = entries;
            defaults = ["EventId", "WorkflowId", "Time", "Source", "Message"];
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
