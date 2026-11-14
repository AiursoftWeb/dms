using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

public sealed class MachineDiskUsageWorkflow(EmptyInput input)
    : MachineWorkflow<EmptyInput, DiskUsageResult>(MachineWorkflows.DiskUsage, input)
{
    public override async Task<DiskUsageResult> ExecuteAsync(MachineWorkflowContext context, EmptyInput input, CancellationToken token)
    {
        // Numeric columns avoid parsing paths, localized headers or filesystem names.
        var output = await context.CallAsync(new ProcessWorkflow("/usr/bin/df",
            ["--local", "--block-size=1", "--exclude-type=tmpfs", "--exclude-type=devtmpfs",
                "--exclude-type=squashfs", "--exclude-type=iso9660", "--exclude-type=overlay", "--exclude-type=aufs",
                "--output=size,used,avail"], captureOutput: true), token);
        var result = JsonSerializer.Deserialize<ProcessResult>(output)
            ?? throw new InvalidDataException("The disk query returned no process result.");
        return Parse(result.Output, context.Id);
    }

    public static DiskUsageResult Parse(string output, Guid workflowId)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2) throw new InvalidDataException("No eligible local filesystems were reported.");
        long total = 0, used = 0, available = 0;
        foreach (var line in lines.Skip(1))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length != 3
                || !long.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)
                || !long.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var occupied)
                || !long.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var free)
                || size < 0 || occupied < 0)
                throw new InvalidDataException("The disk query returned invalid byte counts.");
            checked { total += size; used += occupied; available += free; }
        }
        return new DiskUsageResult(workflowId, total, used, available, lines.Length - 1);
    }
}
