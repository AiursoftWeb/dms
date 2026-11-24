using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

internal sealed class SoapArtifactWorkflows(DmsArtifactStore artifacts)
{
    internal void Register(ClusterWorkflowRegistry registry)
    {
        registry.Register(SoapWorkflows.InspectWsdl, () => new ArtifactWorkflow<SoapContractInput, JsonElement>(InspectAsync));
        registry.Register(SoapWorkflows.DiffWsdl, () => new ArtifactWorkflow<SoapDiffInput, JsonElement>(DiffAsync));
        registry.Register(SoapWorkflows.ValidateXml, () => new ArtifactWorkflow<XmlValidateInput, JsonElement>(ValidateAsync));
        registry.Register(SoapWorkflows.AssertField, () => new ArtifactWorkflow<XmlAssertInput, XmlAssertResult>(AssertAsync));
    }

    private async Task<JsonElement> InspectAsync(WorkflowExecutionContext context, SoapContractInput input, CancellationToken token)
    {
        var bytes = await ReadAsync(context, input.Artifact, token);
        return JsonSerializer.SerializeToElement(SoapContractAnalysis.Inspect(ReadBundle(bytes)));
    }

    private async Task<JsonElement> DiffAsync(WorkflowExecutionContext context, SoapDiffInput input, CancellationToken token)
    {
        var before = await ReadAsync(context, input.Before, token);
        var after = await ReadAsync(context, input.After, token);
        return JsonSerializer.SerializeToElement(SoapContractAnalysis.Diff(ReadBundle(before), ReadBundle(after)));
    }

    private async Task<JsonElement> ValidateAsync(WorkflowExecutionContext context, XmlValidateInput input, CancellationToken token)
    {
        var xml = ReadXml(await ReadAsync(context, input.Xml, token), token);
        var schema = await ReadAsync(context, input.Schema, token);
        var rawXml = schema[0] is (byte)0xFF or 0xFE or 0 || IsXml(schema);
        var bundle = rawXml ? StandaloneSchema(ReadXml(schema, token)) : ReadBundle(schema);
        var result = SoapContractAnalysis.Validate(xml, bundle);
        if (!result.Valid)
        {
            await context.TraceAsync("XML did not satisfy the supplied schema; see the validation result.", token);
            await context.WarningAsync(token);
        }
        return JsonSerializer.SerializeToElement(result);
    }

    private async Task<XmlAssertResult> AssertAsync(WorkflowExecutionContext context, XmlAssertInput input, CancellationToken token)
    {
        var result = AssertField(ReadXml(await ReadAsync(context, input.Artifact, token), token), input.Path, input.Expect);
        if (!result.Passed)
        {
            await context.TraceAsync("Field assertion did not match the response; see the match counts.", token);
            await context.WarningAsync(token);
        }
        return result;
    }

    internal static XmlAssertResult AssertField(XDocument xml, string path, string expect)
    {
        var segments = path[1..].Split('/');
        if (xml.Root is not { } root || root.Name.LocalName != segments[0]) return new(expect == "missing", 0, 0, 0, 0, 0);
        IEnumerable<XElement> nodes = [root];
        foreach (var segment in segments.Skip(1))
            nodes = nodes.SelectMany(node => node.Elements().Where(element => element.Name.LocalName == segment));
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        int matches = 0, empty = 0, nil = 0, nonempty = 0, matchingExpectation = 0;
        foreach (var element in nodes)
        {
            matches++;
            var isNil = ((string?)element.Attribute(xsi + "nil"))?.Trim() is "true" or "1";
            var isEmpty = !isNil && string.IsNullOrWhiteSpace(element.Value);
            if (isNil) nil++;
            else if (isEmpty) empty++;
            else nonempty++;
            if (expect switch
                {
                    "present" => true,
                    "nil" => isNil,
                    "empty" => isEmpty,
                    "nonempty" => !isNil && !isEmpty,
                    "missing" => false,
                    _ => !isNil && element.Value.Equals(expect[7..], StringComparison.Ordinal)
                }) matchingExpectation++;
        }
        return new(expect == "missing" ? matches == 0 : matches > 0 && matches == matchingExpectation,
            matches, empty, nil, nonempty, matchingExpectation);
    }

    private async Task<byte[]> ReadAsync(WorkflowExecutionContext context, ArtifactReference reference, CancellationToken token)
    {
        var requester = await context.GetRequesterAsync(token);
        var stored = artifacts.Get(reference.Id);
        if (stored.OwnerId != requester || !DmsArtifactAccess.Matches(stored.Reference, reference))
            throw new InvalidOperationException("The approved artifact is no longer available to the requester.");
        await using var stream = artifacts.Open(reference.Id);
        if (stream.Length != reference.Length) throw new InvalidOperationException("The approved artifact length changed.");
        var bytes = new byte[checked((int)reference.Length)];
        await stream.ReadExactlyAsync(bytes, token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(reference.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The approved artifact checksum changed.");
        return bytes;
    }

    private static SoapContractBundle ReadBundle(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) data = data[3..];
        try { return JsonSerializer.Deserialize<SoapContractBundle>(data, WorkflowJsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new InvalidOperationException("Expected a SOAP contract bundle from fetch-wsdl."); }
    }

    private static readonly JsonSerializerOptions WorkflowJsonOptions = new()
    {
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private static bool IsXml(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) data = data[3..];
        foreach (var b in data)
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')) return b == (byte)'<';
        return false;
    }

    private static SoapContractBundle StandaloneSchema(XDocument document)
    {
        if (document.Root?.Name != XName.Get("schema", "http://www.w3.org/2001/XMLSchema"))
            throw new InvalidOperationException("The schema artifact must be a standalone XSD or a fetched contract bundle.");
        const string root = "https://dms.invalid/schema.xsd";
        return new SoapContractBundle(root, new Dictionary<string, string> { [root] = document.ToString(SaveOptions.DisableFormatting) });
    }

    private static XDocument ReadXml(byte[] bytes, CancellationToken token)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = SoapLimits.MaxContractBytes
        };
        using var stream = new MemoryStream(bytes, writable: false);
        try
        {
            using (var check = XmlReader.Create(stream, settings))
            {
                var nodes = 0;
                while (check.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (check.Depth > 96 || (nodes += 1 + check.AttributeCount) > 150_000)
                        throw new InvalidOperationException("The XML exceeds the nesting or node limit.");
                }
            }
            stream.Position = 0;
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"Invalid XML at line {exception.LineNumber}, column {exception.LinePosition}.");
        }
    }

    private sealed class ArtifactWorkflow<TInput, TResult>(Func<WorkflowExecutionContext, TInput, CancellationToken, Task<TResult>> execute)
        : ClusterWorkflow<TInput, TResult>
    {
        public override Task<TResult> ExecuteAsync(WorkflowExecutionContext context, TInput input, CancellationToken token) => execute(context, input, token);
    }
}
