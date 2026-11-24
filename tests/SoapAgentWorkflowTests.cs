using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Tests;

[TestClass]
public class SoapAgentWorkflowTests
{
    private const string Root = "https://fixtures.example/service?wsdl";
    private const string Schema = "https://fixtures.example/types.xsd";
    private const string Endpoint = "https://fixtures.example/service";
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string Template = """
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" xmlns:q="urn:fixture">
          <s:Header><q:Context><q:Site>test</q:Site><q:MessageID>{{dmsMessageId}}</q:MessageID></q:Context></s:Header>
          <s:Body><q:Lookup><q:Request><q:Id>{{id}}</q:Id><q:Limit>{{limit}}</q:Limit></q:Request></q:Lookup></s:Body>
        </s:Envelope>
        """;

    [TestMethod]
    public async Task ProfileUsesDocumentedJsonSchemaAndRejectsDuplicateMembers()
    {
        var json = $$"""
            {
              "services": {
                "fixture": {
                  "environment":"test","endpoint":"{{Endpoint}}",
                  "operations": {
                    "lookup": {
                      "mode":"read-only","action":"urn:lookup","template":{{JsonSerializer.Serialize(Template)}},
                      "arguments":{"id":{"type":"string","maxLength":128},"limit":{"type":"integer","min":1,"max":3} },
                      "businessSuccess":{"path":["{urn:fixture}Reply","{urn:fixture}Success"],"equals":"true"}
                    }
                  }
                }
              }
            }
            """;
        var profile = SoapAgentRuntime.ParseProfile(Encoding.UTF8.GetBytes(json)).Services!["fixture"]!;
        var response = $"""
            <s:Envelope xmlns:s="{Soap11}" xmlns:q="urn:fixture"><s:Body><q:Reply><q:Success>true</q:Success></q:Reply></s:Body></s:Envelope>
            """;
        var fixture = new Fixture(profile, (_, _) => Task.FromResult(Response(response)));
        var result = await fixture.Run(SoapWorkflows.Query, fixture.Call());
        Assert.AreEqual("success", result.Outcome);
        Assert.ThrowsExactly<JsonException>(() => SoapAgentRuntime.ParseProfile("""{"services":{},"services":{}}"""u8.ToArray()));
    }

    [TestMethod]
    public async Task FetchBundlesOnlyExplicitSameOriginDependencies()
    {
        var root = """
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <types><xs:schema><xs:include schemaLocation="types.xsd" /></xs:schema></types>
            </definitions>
            """;
        var schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:include schemaLocation="types.xsd" /></xs:schema>
            """;
        var fixture = new Fixture(new SoapServiceProfile { Wsdl = Root, WsdlDependencies = [Schema] }, (request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.IsNull(request.Headers.Authorization);
            return Task.FromResult(Response(request.RequestUri!.AbsoluteUri == Root ? root : schema));
        });
        var result = await fixture.Run(SoapWorkflows.FetchWsdl, new SoapServiceInput("fixture"));
        var bundle = JsonSerializer.Deserialize<SoapContractBundle>(fixture.Artifacts.Read(result.Artifact))!;
        Assert.AreEqual("file", result.Artifact.Kind);
        Assert.AreEqual(2, result.DocumentCount);
        Assert.AreEqual(Root, bundle.Root);
        CollectionAssert.AreEquivalent(new[] { Root, Schema }, bundle.Documents.Keys.ToArray());
        CollectionAssert.AreEqual(new[] { Root, Schema }, fixture.Requests.ToArray());
    }

    [TestMethod]
    public async Task FetchRejectsSchemaReturnedFromWsdlUrl()
    {
        var fixture = new Fixture(new SoapServiceProfile { Wsdl = Root }, (_, _) =>
            Task.FromResult(Response("<schema xmlns='http://www.w3.org/2001/XMLSchema' />")));
        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            fixture.Run(SoapWorkflows.FetchWsdl, new SoapServiceInput("fixture")));
        StringAssert.Contains(error.Message, "did not return a WSDL document");
    }

    [TestMethod]
    [DataRow("unregistered.xsd")]
    [DataRow("https://other.example/types.xsd")]
    [DataRow("types.xsd#anchor")]
    public async Task FetchRefusesUndeclaredDependencyBeforeMakingRequest(string location)
    {
        var fixture = new Fixture(new SoapServiceProfile { Wsdl = Root, WsdlDependencies = [Schema] }, (_, _) => Task.FromResult(Response($"""
            <definitions xmlns="http://schemas.xmlsoap.org/wsdl/" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <types><xs:schema><xs:include schemaLocation="{location}" /></xs:schema></types>
            </definitions>
            """)));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Run(SoapWorkflows.FetchWsdl, new SoapServiceInput("fixture")));
        CollectionAssert.AreEqual(new[] { Root }, fixture.Requests.ToArray());
    }

    [TestMethod]
    public async Task QueryBindsTypedArgumentsAsTextAndRedactsResponse()
    {
        string? body = null;
        var fixture = new Fixture(Profile(), async (request, token) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("text/xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual("\"urn:lookup\"", request.Headers.GetValues("SOAPAction").Single());
            body = await request.Content.ReadAsStringAsync(token);
            return Response($"""
                <s:Envelope xmlns:s="{Soap11}" xmlns:q="urn:fixture" api-key="never-log-attribute">
                  <s:Header><q:Context>never-log-header</q:Context></s:Header>
                  <s:Body><q:Reply><q:Success>true</q:Success><q:Password>never-log-password</q:Password>
                    <q:Nested Session-ID="never-log-session"><!--never-log-comment--><q:Session_Key>never-log-custom</q:Session_Key></q:Nested>
                    <ttContext><propertyName>AccessToken</propertyName><propertyValue>never-log-qad-token</propertyValue></ttContext>
                    <q:ttContext><q:propertyName> partner_Secret_Code </q:propertyName><q:propertyValue><q:Value>never-log-qad-configured</q:Value></q:propertyValue></q:ttContext>
                    <ttContext><propertyName>Site</propertyName><propertyValue>fixture-site</propertyValue></ttContext>
                    <q:NewField>visible</q:NewField></q:Reply></s:Body>
                </s:Envelope>
                """);
        });
        var result = await fixture.Run(SoapWorkflows.Query, fixture.Call("""{"id":"<q:Injected/>&","limit":3}"""));
        Assert.AreEqual("success", result.Outcome);
        Assert.AreEqual(200, result.HttpStatus);
        var xml = XDocument.Parse(body!);
        Assert.AreEqual("<q:Injected/>&", xml.Descendants(XName.Get("Id", "urn:fixture")).Single().Value);
        Assert.AreEqual("test", xml.Descendants(XName.Get("Site", "urn:fixture")).Single().Value);
        var messageId = xml.Descendants(XName.Get("MessageID", "urn:fixture")).Single().Value;
        Assert.StartsWith("urn:uuid:", messageId);
        Assert.IsTrue(Guid.TryParse(messageId[9..], out _));
        Assert.IsFalse(xml.Descendants().Any(e => e.Name.LocalName == "Injected"));
        var saved = fixture.Artifacts.Read(result.Artifact!);
        Assert.Contains("visible", saved);
        var retainedContext = XDocument.Parse(saved).Descendants().Single(e => e.Name.LocalName == "ttContext");
        Assert.AreEqual("Site", retainedContext.Element("propertyName")?.Value);
        Assert.AreEqual("fixture-site", retainedContext.Element("propertyValue")?.Value);
        Assert.Contains("[redacted]", saved);
        Assert.DoesNotContain("never-log", saved);
        Assert.DoesNotContain("never-log", JsonSerializer.Serialize(fixture.Events));
        Assert.IsTrue(fixture.Events.Any(e => e.IsTrace && e.Message.Contains("redaction", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("""{"id":"I1","id":"I2","limit":3}""")]
    [DataRow("""{"id":"I1","limit":9}""")]
    [DataRow("""{"id":"I1","limit":3,"extra":"X"}""")]
    [DataRow("""{"id":"I1","limit":3,"dmsMessageId":"urn:uuid:bad"}""")]
    public async Task QueryRejectsArgumentsThatDoNotMatchRulesWithoutSending(string arguments)
    {
        var fixture = new Fixture(Profile(), UnexpectedRequest);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Run(SoapWorkflows.Query, fixture.Call(arguments)));
        Assert.IsEmpty(fixture.Requests);
    }

    [TestMethod]
    public async Task CallTestRequiresBothExplicitModeAndTestEnvironment()
    {
        var production = new Fixture(Profile("production", "test-only"), UnexpectedRequest);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => production.Run(SoapWorkflows.CallTest, production.Call()));
        var query = new Fixture(Profile(), UnexpectedRequest);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => query.Run(SoapWorkflows.CallTest, query.Call()));
        var testOnly = new Fixture(Profile("test", "test-only"), UnexpectedRequest);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => testOnly.Run(SoapWorkflows.Query, testOnly.Call()));
        Assert.IsEmpty(production.Requests);
        Assert.IsEmpty(query.Requests);
        Assert.IsEmpty(testOnly.Requests);
    }

    [TestMethod]
    [DataRow("soap-fault", 500, true)]
    [DataRow("business-failure", 200, true)]
    [DataRow("http-error", 400, true)]
    [DataRow("invalid-response", 200, false)]
    [DataRow("redirect-refused", 307, false)]
    public async Task CallTestMakesOnePostAndReportsFailureWithoutRawFaultData(string expected, int status, bool publishes)
    {
        var profile = Profile("test", "test-only", Template.Replace(Soap11, Soap12, StringComparison.Ordinal));
        var fixture = new Fixture(profile, (request, _) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("application/soap+xml", request.Content!.Headers.ContentType!.MediaType);
            Assert.AreEqual("\"urn:lookup\"", request.Content.Headers.ContentType.Parameters.Single(p => p.Name == "action").Value);
            Assert.IsFalse(request.Headers.Contains("SOAPAction"));
            var payload = expected switch
            {
                "soap-fault" => "<s:Fault><s:Reason><s:Text>secret-fault-detail</s:Text></s:Reason></s:Fault>",
                "invalid-response" => "<!DOCTYPE forbidden><not-soap/>",
                _ => "<q:Reply><q:Success>false</q:Success></q:Reply>"
            };
            var response = Response(expected == "invalid-response" ? payload : $"""
                <s:Envelope xmlns:s="{Soap12}" xmlns:q="urn:fixture"><s:Body>{payload}</s:Body></s:Envelope>
                """, status);
            if (status == 307) response.Headers.Location = new Uri("https://other.example/target");
            return Task.FromResult(response);
        });
        SoapCallResult result;
        if (expected == "invalid-response")
        {
            await Assert.ThrowsExactlyAsync<WorkflowNeedsVerificationException>(() => fixture.Run(SoapWorkflows.CallTest, fixture.Call()));
            result = SoapWorkflows.CallTest.ReadResult(fixture.Events.Last().Result);
            Assert.AreEqual(RunState.NeedsVerification, fixture.Events.Last().State);
        }
        else
        {
            result = await fixture.Run(SoapWorkflows.CallTest, fixture.Call());
            Assert.AreEqual(RunState.Warning, fixture.Events.Last().State);
        }
        Assert.AreEqual(expected, result.Outcome);
        Assert.AreEqual(status, result.HttpStatus);
        Assert.AreEqual(publishes, result.Artifact is not null);
        CollectionAssert.AreEqual(new[] { Endpoint }, fixture.Requests.ToArray());
        Assert.DoesNotContain("secret-fault-detail", JsonSerializer.Serialize(fixture.Events));
        if (result.Artifact is not null) Assert.DoesNotContain("secret-fault-detail", fixture.Artifacts.Read(result.Artifact));
    }

    [TestMethod]
    [DataRow("transport-error")]
    [DataRow("response-too-large")]
    public async Task CallTestPublishesVerificationStateWhenConfirmationIsUnusable(string expected)
    {
        var fixture = new Fixture(Profile("test", "test-only"), (_, _) =>
        {
            if (expected == "transport-error") throw new HttpRequestException("opaque-transport-detail");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[SoapLimits.MaxXmlBytes + 1]) });
        });
        await Assert.ThrowsExactlyAsync<WorkflowNeedsVerificationException>(() => fixture.Run(SoapWorkflows.CallTest, fixture.Call()));
        var terminal = fixture.Events.Last();
        Assert.AreEqual(RunState.NeedsVerification, terminal.State);
        Assert.AreEqual(expected, SoapWorkflows.CallTest.ReadResult(terminal.Result).Outcome);
        CollectionAssert.AreEqual(new[] { Endpoint }, fixture.Requests.ToArray());
        Assert.DoesNotContain("opaque-transport-detail", JsonSerializer.Serialize(fixture.Events));
    }

    private static Task<HttpResponseMessage> UnexpectedRequest(HttpRequestMessage request, CancellationToken token) =>
        throw new AssertFailedException("Unexpected outbound request.");

    private static SoapServiceProfile Profile(string environment = "test", string mode = "read-only", string template = Template) => new()
    {
        Environment = environment, Endpoint = Endpoint, RedactNames = ["session-key", "PartnerSecretCode"],
        Operations = new()
        {
            ["lookup"] = new SoapOperationProfile(mode, "urn:lookup", template, new()
            {
                ["id"] = new SoapArgumentRule("string", MaxLength: 64),
                ["limit"] = new SoapArgumentRule("integer", Min: 1, Max: 3)
            }, new SoapBusinessSuccess(["{urn:fixture}Reply", "{urn:fixture}Success"], "true"))
        }
    };

    private static HttpResponseMessage Response(string xml, int status = 200) => new((HttpStatusCode)status)
    { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

    private sealed class Fixture(SoapServiceProfile profile, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        internal InMemoryArtifacts Artifacts { get; } = new();
        internal List<string> Requests { get; } = [];
        internal List<WorkflowEvent> Events { get; } = [];
        internal SoapCallInput Call(string args = """{"id":"P1","limit":1}""") =>
            new("fixture", "lookup", Artifacts.Add(args));
        internal async Task<TResult> Run<TInput, TResult>(MachineWorkflowDefinition<TInput, TResult> definition, TInput input)
        {
            var environment = new AgentEnvironment(Guid.NewGuid(), "1", 0, 0, Artifacts: Artifacts);
            var runtime = new SoapAgentRuntime(_ => Task.FromResult(new SoapProfileFile(new() { ["fixture"] = profile })),
                () => new HttpClient(new Handler((request, token) =>
                {
                    Requests.Add(request.RequestUri!.AbsoluteUri);
                    return send(request, token);
                })));
            var registry = new MachineWorkflowRegistry(runtime);
            var context = new MachineWorkflowContext(Guid.NewGuid(), null, e => { Events.Add(e); return Task.CompletedTask; }, environment, registry);
            var workflow = registry.Create(definition.Name, definition.Serialize(input), environment);
            return definition.ReadResult(await context.RunAsync(workflow, CancellationToken.None));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class InMemoryArtifacts : IAgentArtifactTransport
    {
        private readonly Dictionary<Guid, byte[]> _data = [];
        internal ArtifactReference Add(string value) => Add(Encoding.UTF8.GetBytes(value), "json", "file");
        private ArtifactReference Add(byte[] bytes, string name, string kind)
        {
            var id = Guid.NewGuid();
            _data.Add(id, bytes);
            return new ArtifactReference(id, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, kind, name);
        }
        internal string Read(ArtifactReference artifact) => Encoding.UTF8.GetString(_data[artifact.Id]);
        public async Task<ArtifactReference> UploadAsync(Guid workflowId, string file, string name, string kind, CancellationToken token) =>
            Add(await File.ReadAllBytesAsync(file, token), name, kind);
        public Task DownloadAsync(ArtifactReference artifact, string file, CancellationToken token) => File.WriteAllBytesAsync(file, _data[artifact.Id], token);
    }
}
