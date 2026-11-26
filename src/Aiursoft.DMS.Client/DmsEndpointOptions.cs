using System.CommandLine;

namespace Aiursoft.DMS.Client;

internal sealed class DmsEndpointOptions(string description)
{
    internal const string Name = "--dms-server-endpoint";

    private readonly Option<string?> _endpoint = new(Name) { Description = description };
    private readonly Option<string?> _legacyEndpoint = new("--server") { Hidden = true };

    internal IEnumerable<Option> Options => [_endpoint, _legacyEndpoint];

    internal string? GetValue(ParseResult context) =>
        context.GetValue(_endpoint) ?? context.GetValue(_legacyEndpoint);
}
