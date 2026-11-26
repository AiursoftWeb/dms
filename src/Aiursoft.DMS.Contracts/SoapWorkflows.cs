using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Aiursoft.DMS.Contracts;

public static class SoapWorkflows
{
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SoapServiceInput, SoapFetchResult> FetchWsdl = new(
        "machine.soap.fetch-wsdl", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(FetchWsdl)),
        ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly MachineWorkflowDefinition<SoapCallInput, SoapCallResult> Query = new(
        "machine.soap.query", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(Query)),
        ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.High)]
    public static readonly MachineWorkflowDefinition<SoapCallInput, SoapCallResult> CallTest = new(
        "machine.soap.call-test", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(CallTest)),
        ValidateInput: input => input.Validate(), PublicEntry: true) { SingleMachineOnly = true };
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly ClusterWorkflowDefinition<SoapContractInput, JsonElement> InspectWsdl = new(
        "cluster.soap.inspect-wsdl", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(InspectWsdl)),
        ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly ClusterWorkflowDefinition<SoapDiffInput, JsonElement> DiffWsdl = new(
        "cluster.soap.diff-wsdl", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(DiffWsdl)),
        ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly ClusterWorkflowDefinition<XmlValidateInput, JsonElement> ValidateXml = new(
        "cluster.xml.validate", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(ValidateXml)),
        ValidateInput: input => input.Validate(), PublicEntry: true);
    [WorkflowRisk(RiskLevel.Low)]
    public static readonly ClusterWorkflowDefinition<XmlAssertInput, XmlAssertResult> AssertField = new(
        "cluster.xml.assert-field", WorkflowMetadata.Risk(typeof(SoapWorkflows), nameof(AssertField)),
        ValidateInput: input => input.Validate(), PublicEntry: true);
}

public static class SoapLimits
{
    public const int MaxXmlBytes = 4 * 1024 * 1024;
    public const int MaxContractBytes = 8 * 1024 * 1024;
    public const int MaxArgsBytes = 64 * 1024;
    public const int MaxDocuments = 24;

    internal static void File(ArtifactReference artifact, int maxBytes)
    {
        if (artifact is null || artifact.Sha256 is null) throw new ArgumentException("A file artifact with a checksum is required.");
        artifact.Validate();
        if (artifact.Kind != "file" || artifact.Length < 1 || artifact.Length > maxBytes)
            throw new ArgumentException($"Expected a nonempty file artifact of at most {maxBytes} bytes.");
    }
}

public sealed record SoapServiceInput(string Service)
{
    public void Validate() => OperationValidation.Name(Service);
}
public sealed record SoapCallInput(string Service, string Operation, ArtifactReference Artifact) : IArtifactOperationInput
{
    public void Validate()
    {
        OperationValidation.Name(Service);
        OperationValidation.Name(Operation);
        SoapLimits.File(Artifact, SoapLimits.MaxArgsBytes);
    }
}
public sealed record SoapFetchResult(ArtifactReference Artifact, int DocumentCount);
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Workflow result properties are serialized for API and CLI clients.")]
public sealed record SoapCallResult(int? HttpStatus, long ElapsedMilliseconds, string Outcome, ArtifactReference? Artifact);
public sealed record SoapContractBundle(string Root, Dictionary<string, string> Documents);

public sealed record SoapContractInput(ArtifactReference Artifact) : IArtifactOperationInput
{
    public void Validate() => SoapLimits.File(Artifact, SoapLimits.MaxContractBytes);
}
public sealed record SoapDiffInput(ArtifactReference Before, ArtifactReference After) : IMultipleArtifactOperationInput
{
    IEnumerable<ArtifactReference> IMultipleArtifactOperationInput.Artifacts => [Before, After];
    public void Validate()
    {
        SoapLimits.File(Before, SoapLimits.MaxContractBytes);
        SoapLimits.File(After, SoapLimits.MaxContractBytes);
    }
}
public sealed record XmlValidateInput(ArtifactReference Xml, ArtifactReference Schema) : IMultipleArtifactOperationInput
{
    IEnumerable<ArtifactReference> IMultipleArtifactOperationInput.Artifacts => [Xml, Schema];
    public void Validate()
    {
        SoapLimits.File(Xml, SoapLimits.MaxXmlBytes);
        SoapLimits.File(Schema, SoapLimits.MaxContractBytes);
    }
}
public sealed record XmlAssertInput(ArtifactReference Artifact, string Path, string Expect) : IArtifactOperationInput
{
    public void Validate()
    {
        SoapLimits.File(Artifact, SoapLimits.MaxXmlBytes);
        ParsePath(Path);
        if (string.IsNullOrEmpty(Expect) || Expect.Length > 1024 || (Expect is not ("missing" or "empty" or "nonempty" or "nil" or "present")
                && !Expect.StartsWith("equals:", StringComparison.Ordinal)))
            throw new ArgumentException("Expect missing, empty, nonempty, nil, present, or equals:<text>.");
    }

    public static XName[] ParsePath(string? path)
    {
        if (path is not { Length: > 1 and <= 1024 } || path[0] != '/') throw InvalidPath();
        var segments = new List<XName>();
        for (var start = 1; ;)
        {
            var localStart = start;
            var ns = XNamespace.None;
            if (start < path.Length && path[start] == '{')
            {
                var close = path.IndexOf('}', start + 1);
                if (close <= start + 1) throw InvalidPath();
                var uri = path[(start + 1)..close];
                if (uri.Contains('{') || uri.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw InvalidPath();
                ns = uri;
                localStart = close + 1;
            }
            var slash = path.IndexOf('/', localStart);
            var end = slash < 0 ? path.Length : slash;
            if (localStart == end || segments.Count == 32) throw InvalidPath();
            try { segments.Add(ns + XmlConvert.VerifyNCName(path[localStart..end])); }
            catch (XmlException) { throw InvalidPath(); }
            if (slash < 0) return segments.ToArray();
            start = slash + 1;
        }
    }

    private static ArgumentException InvalidPath() =>
        new("Use an absolute XML path of up to 32 elements, with names like {namespace-uri}name or unqualified name, not XPath.");
}
[SuppressMessage("ReSharper", "NotAccessedPositionalProperty.Global", Justification = "Workflow result properties are serialized for API and CLI clients.")]
public sealed record XmlAssertResult(bool Passed, int Matches, int Empty, int Nil, int Nonempty, int MatchingExpectation,
    int Parents = 0, int Missing = 0);
