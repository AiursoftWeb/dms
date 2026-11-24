using System.CommandLine;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Client;

public static partial class DmsCommandApp
{
    private static ActionCommand SoapFetchCommand()
    {
        var service = new Option<string>("--service") { Required = true, Description = "Service name configured on the target Agent." };
        return StandardCommand("fetch-wsdl", "Save the registered WSDL and allowed dependencies as a contract artifact.",
            SoapWorkflows.FetchWsdl, p => new SoapServiceInput(p.GetValue(service)!), service);
    }

    private static ActionCommand SoapCallCommand(bool test)
    {
        var service = new Option<string>("--service") { Required = true, Description = "Service name configured on the target Agent." };
        var operation = new Option<string>("--operation") { Required = true, Description = "Registered SOAP operation name." };
        var argsFile = new Option<string>("--args-file") { Required = true, Description = "Local JSON file containing the registered operation's arguments." };
        return PreparedOperation(test ? "call-test" : "query", test
                ? "Call a registered operation on a test-only endpoint."
                : "Call a registered read-only operation and save its redacted response.",
            test ? SoapWorkflows.CallTest : SoapWorkflows.Query,
            async (client, p, requestId) => new SoapCallInput(p.GetValue(service)!, p.GetValue(operation)!,
                await PrepareOperationTextAsync(client, p.GetValue(argsFile)!, SoapLimits.MaxArgsBytes, requestId)),
            service, operation, argsFile);
    }

    private static ActionCommand SoapInspectCommand()
    {
        var artifact = SoapArtifactOption("--artifact-id", "Saved contract artifact ID.");
        return PreparedOperation("inspect-wsdl", "List operations and fields declared in a saved contract.", SoapWorkflows.InspectWsdl,
            async (client, p, _) => new SoapContractInput(await client.Artifacts.GetAsync(p.GetValue(artifact))), artifact);
    }

    private static ActionCommand SoapDiffCommand()
    {
        var before = SoapArtifactOption("--before", "Original contract artifact ID.");
        var after = SoapArtifactOption("--after", "Updated contract artifact ID.");
        return PreparedOperation("diff-wsdl", "Compare operations and fields in two saved contracts.", SoapWorkflows.DiffWsdl,
            async (client, p, _) => new SoapDiffInput(await client.Artifacts.GetAsync(p.GetValue(before)),
                await client.Artifacts.GetAsync(p.GetValue(after))), before, after);
    }

    private static ActionCommand XmlValidateCommand()
    {
        var xml = SoapArtifactOption("--xml-artifact-id", "Saved XML file artifact ID.");
        var schema = SoapArtifactOption("--schema-artifact-id", "Saved standalone XSD or SOAP contract artifact ID.");
        return PreparedOperation("validate", "Validate saved XML against an XSD without contacting the original service.",
            SoapWorkflows.ValidateXml, async (client, p, _) => new XmlValidateInput(await client.Artifacts.GetAsync(p.GetValue(xml)),
                await client.Artifacts.GetAsync(p.GetValue(schema))), xml, schema);
    }

    private static ActionCommand XmlAssertCommand()
    {
        var artifact = SoapArtifactOption("--artifact-id", "Saved XML file artifact ID.");
        var path = new Option<string>("--path") { Required = true, Description = "Absolute element path, e.g. /{urn:example}response/{urn:example}field (no XPath)." };
        var expect = new Option<string>("--expect") { Required = true, Description = "missing, empty, nonempty, nil, present, or equals:<text>." };
        return PreparedOperation("assert-field", "Check matching elements in a saved XML file.", SoapWorkflows.AssertField,
            async (client, p, _) => new XmlAssertInput(await client.Artifacts.GetAsync(p.GetValue(artifact)),
                p.GetValue(path)!, p.GetValue(expect)!), artifact, path, expect);
    }

    private static Option<Guid> SoapArtifactOption(string name, string description) => new(name) { Required = true, Description = description };
}
