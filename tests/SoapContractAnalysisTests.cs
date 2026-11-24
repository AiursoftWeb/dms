using System.Text.Json;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Services.Dms;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class SoapContractAnalysisTests
{
    private const string WsdlRoot = "https://fixture.invalid/api/main.wsdl";
    private const string SchemaRoot = "https://fixture.invalid/api/main.xsd";
    private const string CommonSchema = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:types"
                   targetNamespace="urn:types" elementFormDefault="qualified">
          <xs:complexType name="Details">
            <xs:sequence><xs:element name="code" type="xs:int"/></xs:sequence>
            <xs:attribute name="status" type="xs:string" use="required"/>
          </xs:complexType>
        </xs:schema>
        """;

    [TestMethod]
    public void InspectsImportedMessagesEmbeddedSchemasAndIncludedTypes()
    {
        var root = """
            <w:definitions xmlns:w="http://schemas.xmlsoap.org/wsdl/" xmlns:m="urn:messages" targetNamespace="urn:service">
              <w:import namespace="urn:messages" location="contracts/messages.wsdl"/>
              <w:portType name="Service">
                <w:operation name="Read"><w:input message="m:ReadIn"/><w:output message="m:ReadOut"/></w:operation>
              </w:portType>
            </w:definitions>
            """;
        var imported = """
            <w:definitions xmlns:w="http://schemas.xmlsoap.org/wsdl/" xmlns:x="http://www.w3.org/2001/XMLSchema"
                           xmlns:t="urn:types" targetNamespace="urn:messages">
              <w:types>
                <x:schema targetNamespace="urn:types" elementFormDefault="qualified">
                  <x:include schemaLocation="../common.xsd"/>
                  <x:element name="Request">
                    <x:complexType><x:sequence>
                      <x:element name="details" type="t:Details" minOccurs="0" maxOccurs="unbounded"/>
                      <x:choice><x:element name="id" type="x:int"/><x:element name="key" type="x:string"/></x:choice>
                    </x:sequence></x:complexType>
                  </x:element>
                </x:schema>
              </w:types>
              <w:message name="ReadIn"><w:part name="parameters" element="t:Request"/></w:message>
              <w:message name="ReadOut"><w:part name="value" type="t:Details"/></w:message>
            </w:definitions>
            """;
        var contract = new SoapContractBundle(WsdlRoot, new()
        {
            [WsdlRoot] = root,
            ["https://fixture.invalid/api/contracts/messages.wsdl"] = imported,
            ["https://fixture.invalid/api/common.xsd"] = CommonSchema
        });

        var inspection = SoapContractAnalysis.Inspect(contract);
        var operation = inspection.Operations.Single();
        Assert.AreEqual("{urn:service}Service", operation.PortType);
        Assert.AreEqual("Read", operation.Name);
        var request = operation.Request!.Fields.Single();
        Assert.AreEqual("{urn:types}Request", request.Name);
        Assert.AreEqual("parameters", request.Part);
        Assert.IsTrue(request.Required);
        var details = request.Fields[0];
        Assert.IsFalse(details.Required);
        Assert.AreEqual("unbounded", details.MaxOccurs);
        Assert.AreEqual("{urn:types}Details", details.Type);
        Assert.IsTrue(details.Fields[0].Required);
        Assert.AreEqual("{http://www.w3.org/2001/XMLSchema}int", details.Fields[0].Type);
        Assert.IsFalse(request.Fields[1].Required);
        var attribute = operation.Response!.Fields.Single().Fields.Single(field => field.Kind == "attribute");
        Assert.AreEqual("status", attribute.Name);
        Assert.IsTrue(attribute.Required);
        Assert.IsTrue(inspection.Warnings.Any(warning => warning.Contains("Choice", StringComparison.Ordinal)));
        Assert.IsTrue(SoapContractAnalysis.Validate(XDocument.Parse("<Request xmlns='urn:types'><id>1</id></Request>"), contract).Valid);
    }

    [TestMethod]
    public void DiffsFieldShapeAndOperationsWithoutComparingNamespacePrefixes()
    {
        var before = Bundle(WsdlRoot, Wsdl("int", "0", "Remove"));
        var after = Bundle(WsdlRoot, Wsdl("string", "1", "Add").Replace("x:", "s:", StringComparison.Ordinal)
            .Replace("xmlns:x=", "xmlns:s=", StringComparison.Ordinal));
        var diff = SoapContractAnalysis.Diff(before, after);
        CollectionAssert.AreEquivalent(new[]
        {
            "operation-added", "operation-removed", "field-type-changed", "field-required-changed", "field-min-occurs-changed"
        }, diff.Changes.Select(change => change.Kind).ToArray());
        var type = diff.Changes.Single(change => change.Kind == "field-type-changed");
        Assert.AreEqual("request", type.Direction);
        CollectionAssert.AreEqual(new[] { "parameters", "{urn:fixture}value" }, type.Path.ToArray());
        Assert.AreEqual("{http://www.w3.org/2001/XMLSchema}int", type.Before);
        Assert.AreEqual("{http://www.w3.org/2001/XMLSchema}string", type.After);
    }

    [TestMethod]
    public void ValidatesStandaloneBundledSchemaWithoutLeakingInstanceDataOrFollowingHints()
    {
        var schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:types"
                       targetNamespace="urn:types" elementFormDefault="qualified">
              <xs:include schemaLocation="common.xsd"/>
              <xs:element name="details" type="t:Details"/>
            </xs:schema>
            """;
        var bundle = Bundle(SchemaRoot, schema);
        bundle.Documents["https://fixture.invalid/api/common.xsd"] = CommonSchema;
        var valid = XDocument.Parse("""
            <details xmlns="urn:types" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                     xsi:schemaLocation="urn:types https://not-in-bundle.invalid/schema.xsd" status="ok"><code>12</code></details>
            """);
        Assert.IsTrue(SoapContractAnalysis.Validate(valid, bundle).Valid);

        var invalid = XDocument.Parse("""
            <details xmlns="urn:types" status="ok">
              <code>SENSITIVE_INSTANCE_VALUE</code>
            </details>
            """, LoadOptions.SetLineInfo);
        var originalCulture = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new("fr-FR");
            var result = SoapContractAnalysis.Validate(invalid, bundle);
            Assert.IsFalse(result.Valid);
            Assert.AreEqual("fr-FR", System.Globalization.CultureInfo.CurrentUICulture.Name);
            var error = result.Errors.Single();
            Assert.AreEqual("type-mismatch", error.Code);
            Assert.AreEqual(2, error.Line);
            Assert.IsGreaterThan(0, error.Column);
            Assert.DoesNotContain("SENSITIVE_INSTANCE_VALUE", JsonSerializer.Serialize(result));
            var missing = SoapContractAnalysis.Validate(XDocument.Parse("<details xmlns='urn:types'/>"), bundle);
            CollectionAssert.AreEquivalent(new[] { "required-attribute", "incomplete-content" }, missing.Errors.Select(item => item.Code).ToArray());
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = originalCulture; }
        var unexpected = SoapContractAnalysis.Validate(XDocument.Parse("<details xmlns='urn:types' status='ok'><SENSITIVE_NODE/></details>"), bundle);
        Assert.IsTrue(unexpected.Errors.Any(item => item.Code == "invalid-structure"));
        Assert.DoesNotContain("SENSITIVE_NODE", JsonSerializer.Serialize(unexpected));
        Assert.AreEqual("unknown-root", SoapContractAnalysis.Validate(XDocument.Parse("<other/>"), bundle).Errors.Single().Code);
    }

    [TestMethod]
    public void HandlesNoLocationImportAndMultipleChameleonIncludes()
    {
        var bundle = Bundle(SchemaRoot, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:test" xmlns:c="urn:common"
                       targetNamespace="urn:test" elementFormDefault="qualified">
              <xs:import namespace="urn:common"/>
              <xs:include schemaLocation="one.xsd"/>
              <xs:include schemaLocation="two.xsd"/>
              <xs:element name="root"><xs:complexType><xs:sequence>
                <xs:element name="one" type="t:One"/>
                <xs:element name="two" type="t:Two"/>
                <xs:element name="three" type="c:Three"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        bundle.Documents.Add("https://fixture.invalid/api/one.xsd", Chameleon("One"));
        bundle.Documents.Add("https://fixture.invalid/api/two.xsd", Chameleon("Two"));
        bundle.Documents.Add("https://fixture.invalid/api/common.xsd", Chameleon("Three").Replace("<xs:schema ",
            "<xs:schema targetNamespace='urn:common' ", StringComparison.Ordinal));
        var result = SoapContractAnalysis.Validate(XDocument.Parse("<root xmlns='urn:test'><one>1</one><two>2</two><three>3</three></root>"), bundle);
        Assert.IsTrue(result.Valid, JsonSerializer.Serialize(result));

        static string Chameleon(string name) => $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:simpleType name="{name}"><xs:restriction base="xs:int"/></xs:simpleType>
            </xs:schema>
            """;
    }

    [TestMethod]
    public void RejectsMissingDependenciesAndDoctypesWithSafeDiagnostics()
    {
        var missing = Bundle(SchemaRoot, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:include schemaLocation="https://not-in-bundle.invalid/private.xsd"/>
            </xs:schema>
            """);
        var xml = XDocument.Parse("<item/>");
        var error = SoapContractAnalysis.Validate(xml, missing).Errors.Single();
        Assert.AreEqual("invalid-schema", error.Code);
        Assert.DoesNotContain("private.xsd", error.Message);

        var dtd = Bundle(SchemaRoot, """
            <!DOCTYPE xs:schema SYSTEM "file:///INVALID_PRIVATE_PATH">
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"/>
            """);
        error = SoapContractAnalysis.Validate(xml, dtd).Errors.Single();
        Assert.AreEqual("invalid-schema", error.Code);
        Assert.DoesNotContain("INVALID_PRIVATE_PATH", error.Message);
        var instanceWithDtd = new XDocument(new XDocumentType("item", null, null, null), new XElement("item"));
        Assert.AreEqual("invalid-xml", SoapContractAnalysis.Validate(instanceWithDtd, Bundle(SchemaRoot, CommonSchema)).Errors.Single().Code);
    }

    [TestMethod]
    public void RejectsUnsupportedRootsAndBoundsRecordCountsAndNestedWork()
    {
        var wsdl2 = Bundle(WsdlRoot, "<description xmlns='http://www.w3.org/ns/wsdl'/>");
        Assert.ThrowsExactly<InvalidDataException>(() => SoapContractAnalysis.Inspect(wsdl2));
        var tooManyDocuments = Bundle(WsdlRoot, Wsdl("int", "0", "ReadOther"));
        for (var i = 0; i < SoapLimits.MaxDocuments; i++) tooManyDocuments.Documents[$"{i}.xsd"] = CommonSchema;
        Assert.ThrowsExactly<InvalidDataException>(() => SoapContractAnalysis.Inspect(tooManyDocuments));

        var tooDeep = Bundle(WsdlRoot, string.Concat(Enumerable.Repeat("<item>", 70)) + string.Concat(Enumerable.Repeat("</item>", 70)));
        var exception = Assert.ThrowsExactly<InvalidDataException>(() => SoapContractAnalysis.Inspect(tooDeep));
        Assert.Contains("nesting", exception.Message);
    }

    [TestMethod]
    public void ValidationBoundsDiagnosticsAndRejectsUnsupportedPatternWork()
    {
        var schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="root"><xs:complexType><xs:sequence>
                <xs:element name="code" minOccurs="0" maxOccurs="unbounded" type="xs:int"/>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        var xml = new XDocument(new XElement("root", Enumerable.Range(0, 40).Select(_ => new XElement("code", "bad"))));
        var result = SoapContractAnalysis.Validate(xml, Bundle(SchemaRoot, schema));
        Assert.IsFalse(result.Valid);
        Assert.IsTrue(result.Truncated);
        Assert.IsLessThan(40, result.Errors.Count);
        Assert.IsTrue(result.Errors.All(error => error.Code == "type-mismatch"));

        var pattern = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="code"><xs:simpleType><xs:restriction base="xs:string">
                <xs:pattern value="(a+)+"/>
              </xs:restriction></xs:simpleType></xs:element>
            </xs:schema>
            """;
        var error = SoapContractAnalysis.Validate(XDocument.Parse("<code>a</code>"), Bundle(SchemaRoot, pattern)).Errors.Single();
        Assert.AreEqual("invalid-schema", error.Code);
        Assert.Contains("complexity", error.Message);
    }

    private static SoapContractBundle Bundle(string root, string document) => new(root, new() { [root] = document });

    private static string Wsdl(string type, string min, string otherOperation) => $"""
        <w:definitions xmlns:w="http://schemas.xmlsoap.org/wsdl/" xmlns:x="http://www.w3.org/2001/XMLSchema"
                       xmlns:t="urn:fixture" targetNamespace="urn:fixture">
          <w:types><x:schema targetNamespace="urn:fixture" elementFormDefault="qualified">
            <x:element name="Request"><x:complexType><x:sequence>
              <x:element name="value" type="x:{type}" minOccurs="{min}"/>
            </x:sequence></x:complexType></x:element>
          </x:schema></w:types>
          <w:message name="ReadIn"><w:part name="parameters" element="t:Request"/></w:message>
          <w:portType name="Service">
            <w:operation name="Read"><w:input message="t:ReadIn"/></w:operation>
            <w:operation name="{otherOperation}"><w:input message="t:ReadIn"/></w:operation>
          </w:portType>
        </w:definitions>
        """;
}
