using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aiursoft.DMS.Contracts;
using Aiursoft.DMS.Workflows.Operations;

namespace Aiursoft.DMS.Workflows;

internal sealed class SoapFetchWorkflow(SoapServiceInput input, AgentEnvironment agent, SoapAgentRuntime runtime)
    : MachineWorkflow<SoapServiceInput, SoapFetchResult>(SoapWorkflows.FetchWsdl, input)
{
    public override async Task<SoapFetchResult> ExecuteAsync(MachineWorkflowContext context, SoapServiceInput input, CancellationToken token)
    {
        var artifacts = SoapAgentArtifacts.Require(agent);
        var profile = await runtime.GetServiceAsync(input.Service, token);
        var root = SoapAgentRuntime.RegisteredUrl(profile.Wsdl, metadata: true);
        if (profile.WsdlDependencies is null || profile.WsdlDependencies.Length >= SoapLimits.MaxDocuments)
            throw new InvalidDataException("The SOAP metadata dependency allowlist exceeds its limit.");
        Dictionary<string, Uri> approved = new(StringComparer.Ordinal) { [root.AbsoluteUri] = root };
        foreach (var configured in profile.WsdlDependencies)
        {
            var uri = SoapAgentRuntime.RegisteredUrl(configured, metadata: true);
            if (!SoapAgentRuntime.SameOrigin(root, uri) || !approved.TryAdd(uri.AbsoluteUri, uri))
                throw new InvalidDataException("Registered SOAP metadata dependencies must be unique and share the root origin.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var fetchToken = timeout.Token;
        using var client = runtime.CreateClient();
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        HashSet<string> discovered = new(StringComparer.Ordinal) { root.AbsoluteUri };
        Queue<Uri> pending = new();
        pending.Enqueue(root);
        var remaining = SoapLimits.MaxContractBytes;
        try
        {
            // Metadata is intentionally fetched without the invocation credential.
            while (pending.TryDequeue(out var uri))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, fetchToken);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidDataException($"A registered SOAP metadata endpoint returned HTTP {(int)response.StatusCode}; redirects are not followed.");
                var bytes = await SoapAgentArtifacts.ReadResponseAsync(response, Math.Min(SoapLimits.MaxXmlBytes, remaining), fetchToken);
                remaining -= bytes.Length;
                var document = SoapAgentXml.Parse(bytes);
                if (uri == root && !SoapAgentXml.IsWsdl(document))
                    throw new InvalidDataException("The registered WSDL URL did not return a WSDL document.");
                foreach (var reference in SoapAgentXml.MetadataReferences(document))
                {
                    if (!Uri.TryCreate(uri, reference, out var resolved) || !SoapAgentRuntime.SafeUrl(resolved, metadata: true)
                        || !SoapAgentRuntime.SameOrigin(root, resolved) || !approved.TryGetValue(resolved.AbsoluteUri, out var dependency))
                        throw new InvalidDataException("A SOAP metadata document references an unapproved dependency.");
                    if (discovered.Add(dependency.AbsoluteUri)) pending.Enqueue(dependency);
                }
                documents.Add(uri.AbsoluteUri, Encoding.UTF8.GetString(SoapAgentXml.Utf8(document)));
            }
            fetchToken.ThrowIfCancellationRequested();
        }
        catch (XmlException) { throw new InvalidDataException("A registered SOAP metadata document contains invalid or prohibited XML."); }
        catch (SoapPayloadTooLargeException) { throw new InvalidDataException("Registered SOAP metadata exceeds its document or bundle size limit."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InvalidDataException("The registered SOAP metadata fetch timed out."); }
        catch (Exception e) when (e is IOException or HttpRequestException) { throw new InvalidDataException("The registered SOAP metadata could not be fetched."); }

        var bytesToPublish = JsonSerializer.SerializeToUtf8Bytes(new SoapContractBundle(root.AbsoluteUri, documents));
        if (bytesToPublish.Length > SoapLimits.MaxContractBytes) throw new InvalidDataException("The serialized SOAP metadata bundle exceeds its size limit.");
        var artifact = await SoapAgentArtifacts.PublishAsync(artifacts, context.Id, bytesToPublish, "soap-contract.json", token);
        return new SoapFetchResult(artifact, documents.Count);
    }
}

internal sealed class SoapInvokeWorkflow(MachineWorkflowDefinition<SoapCallInput, SoapCallResult> definition, SoapCallInput input,
    AgentEnvironment agent, SoapAgentRuntime runtime, bool testOnly) : MachineWorkflow<SoapCallInput, SoapCallResult>(definition, input)
{
    public override async Task<SoapCallResult> ExecuteAsync(MachineWorkflowContext context, SoapCallInput input, CancellationToken token)
    {
        var profile = await runtime.GetServiceAsync(input.Service, token);
        if (profile.Operations is null || !profile.Operations.TryGetValue(input.Operation, out var operation) || operation is null
            || operation.Mode != (testOnly ? "test-only" : "read-only") || (testOnly && profile.Environment != "test"))
            throw new InvalidOperationException("The SOAP operation is not registered for this workflow mode and environment.");
        var endpoint = SoapAgentRuntime.RegisteredUrl(profile.Endpoint, metadata: false);
        var template = new SoapRequestTemplate(operation);
        var redaction = SoapAgentXml.RedactionNames(profile.RedactNames);
        var artifacts = SoapAgentArtifacts.Require(agent);
        string args;
        try { args = await FilesystemOperations.ReadArtifactTextAsync(input.Artifact, agent, SoapLimits.MaxArgsBytes, token); }
        catch (Exception e) when (e is IOException or InvalidDataException or HttpRequestException or DecoderFallbackException)
        { throw new InvalidDataException("The approved SOAP arguments could not be read or verified."); }
        var requestBytes = template.Bind(args);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        var requestToken = timeout.Token;
        int? httpStatus = null;
        byte[]? sanitized = null;
        var elapsed = Stopwatch.StartNew();
        string outcome;
        var sent = false;
        try
        {
            var authorization = await SoapAgentRuntime.ReadAuthorizationAsync(profile.BasicAuth, endpoint, requestToken);
            using var client = runtime.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            // Never reuse a connection for an invocation or follow a redirect/auth challenge.
            request.Version = HttpVersion.Version11;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Content = new ByteArrayContent(requestBytes);
            request.Headers.ConnectionClose = true;
            request.Headers.Authorization = authorization?.Header;
            if (template.EnvelopeNamespace == SoapAgentXml.Soap11)
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
                request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{template.Action}\"");
            }
            else
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
                if (template.Action.Length > 0)
                    request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("action", $"\"{template.Action}\""));
            }
            requestToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
            httpStatus = (int)response.StatusCode;
            if (httpStatus is >= 300 and < 400) outcome = "redirect-refused";
            else
            {
                outcome = response.IsSuccessStatusCode ? "invalid-response" : "http-error";
                try
                {
                    var bytes = await SoapAgentArtifacts.ReadResponseAsync(response, SoapLimits.MaxXmlBytes, requestToken);
                    var xml = SoapAgentXml.Parse(bytes);
                    var (header, body) = SoapAgentXml.Envelope(xml);
                    if (body.Name.Namespace != template.EnvelopeNamespace || body.Elements().Count() != 1)
                        throw new InvalidDataException("Unexpected SOAP response envelope or body.");
                    var fault = body.Element(body.Name.Namespace + "Fault") is not null;
                    outcome = fault ? "soap-fault" : !response.IsSuccessStatusCode ? "http-error" : !template.IsBusinessSuccess(body)
                        ? "business-failure" : "success";
                    sanitized = SoapAgentXml.Redact(xml, header, fault, redaction, authorization);
                }
                catch (SoapPayloadTooLargeException) { if (response.IsSuccessStatusCode) outcome = "response-too-large"; }
                catch (Exception e) when (e is XmlException or InvalidDataException) { /* Preserve the HTTP status classification. */ }
            }
            requestToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { outcome = "timeout"; sanitized = null; }
        catch (OperationCanceledException) when (testOnly && sent) { outcome = "cancelled-after-send"; sanitized = null; }
        catch (Exception e) when (e is HttpRequestException or IOException) { outcome = "transport-error"; sanitized = null; }
        elapsed.Stop();
        if (testOnly && sent && outcome is "timeout" or "transport-error" or "invalid-response" or "response-too-large" or "cancelled-after-send")
        {
            await context.WarningAsync($"Registered test-only SOAP request outcome: {outcome}; verify effects before considering another call.");
            throw new WorkflowNeedsVerificationException(context.Id)
            {
                Result = JsonSerializer.Serialize(new SoapCallResult(httpStatus, elapsed.ElapsedMilliseconds, outcome, null))
            };
        }
        token.ThrowIfCancellationRequested();

        ArtifactReference? artifact = null;
        if (sanitized is not null)
        {
            try
            {
                artifact = await SoapAgentArtifacts.PublishAsync(artifacts, context.Id, sanitized, "soap-response.xml", token);
                await context.TraceAsync("SOAP response artifact redaction: headers, fault details and sensitive QAD context entries removed; sensitive XML names masked.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                await context.WarningAsync("The SOAP request completed, but its redacted response artifact could not be published.");
            }
        }
        else if (outcome is "success" or "business-failure" or "soap-fault")
        {
            await context.WarningAsync("The SOAP response could not be published within the redaction and size limits.");
        }
        if (outcome != "success") await context.WarningAsync($"Registered SOAP request outcome: {outcome}.");
        return new SoapCallResult(httpStatus, elapsed.ElapsedMilliseconds, outcome, artifact);
    }
}

internal static class SoapAgentArtifacts
{
    internal static IAgentArtifactTransport Require(AgentEnvironment agent) => agent.Artifacts
        ?? throw new NotSupportedException("The Agent host has no artifact transport.");

    internal static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new SoapPayloadTooLargeException();
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        return await SoapAgentXml.ReadBoundedAsync(stream, maximum, token);
    }

    internal static async Task<ArtifactReference> PublishAsync(IAgentArtifactTransport artifacts, Guid workflowId, byte[] bytes,
        string name, CancellationToken token)
    {
        var directory = SafeOperationPaths.TemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, name);
            await File.WriteAllBytesAsync(path, bytes, token);
            return await artifacts.UploadAsync(workflowId, path, name, "file", token);
        }
        finally { Directory.Delete(directory, true); }
    }
}
