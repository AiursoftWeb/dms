using System.Text.RegularExpressions;

namespace Aiursoft.DMS.Workflows;

internal static partial class FrpsLayout
{
    internal const string Unit = "frps.service";
    internal const string WrappedUnit = "turn.service";
    internal const string Wrapper = "/usr/bin/turn";
    internal const string WrappedBinary = "/usr/lib/turn/turn";
    internal const string WrappedConfig = "/etc/turn/turn.toml";

    [GeneratedRegex(@"(?m)^ExecStart=\{ path=(?<binary>/[\w./-]+) ; argv\[\]=(?<argv>/[\w./-]+) (?:-c |--config[ =])(?<config>/[\w./-]+) ; [^\n{}]+\}\r?$")]
    internal static partial Regex ExecStart();
}
