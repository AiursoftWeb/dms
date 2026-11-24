using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

internal sealed class SoapAgentRuntime(
    Func<CancellationToken, Task<SoapProfileFile>> loadProfiles,
    Func<HttpClient> createClient)
{
    private const string ProfilePath = "/etc/aiursoft-dms-agent/soap-services.json";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions ProfileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    internal static SoapAgentRuntime Default { get; } = new(LoadProfilesAsync, () => new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        MaxResponseHeadersLength = 32
    }) { Timeout = Timeout.InfiniteTimeSpan });

    internal HttpClient CreateClient() => createClient();

    internal async Task<SoapServiceProfile> GetServiceAsync(string name, CancellationToken token)
    {
        var profiles = await loadProfiles(token);
        if (profiles.Services is null || !profiles.Services.TryGetValue(name, out var service) || service is null)
            throw new InvalidOperationException("The SOAP service is not registered on this Agent.");
        if (service.TimeoutSeconds is < 1 or > 120 || service.Environment is not ("production" or "test"))
            throw new InvalidDataException("The Agent SOAP service has invalid environment or timeout settings.");
        return service;
    }

    private static async Task<SoapProfileFile> LoadProfilesAsync(CancellationToken token)
    {
        try
        {
            var bytes = await ReadLocalFileAsync(ProfilePath, 512 * 1024, ownerOnly: false, token);
            return ParseProfile(bytes);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("The Agent SOAP profile file is missing, unsafe, or invalid.");
        }
    }

    internal static SoapProfileFile ParseProfile(byte[] bytes) =>
        JsonSerializer.Deserialize<SoapProfileFile>(bytes, ProfileJson) ?? throw new InvalidDataException();

    internal static async Task<SoapAuthorization?> ReadAuthorizationAsync(SoapBasicAuth? basic, Uri endpoint, CancellationToken token)
    {
        if (basic is null) return null;
        if (endpoint.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(basic.Username) || basic.Username.Length > 256
            || basic.Username.Contains(':') || basic.Username.Any(char.IsControl) || string.IsNullOrWhiteSpace(basic.PasswordFile)
            || !Path.IsPathFullyQualified(basic.PasswordFile))
            throw new InvalidDataException("SOAP Basic authentication requires HTTPS, a username, and an absolute private password file.");
        try
        {
            var bytes = await ReadLocalFileAsync(basic.PasswordFile, 4096, ownerOnly: true, token);
            var password = Utf8.GetString(bytes).TrimEnd('\r', '\n');
            if (password.Length == 0 || password.Any(char.IsControl)) throw new InvalidDataException();
            var parameter = Convert.ToBase64String(Utf8.GetBytes(basic.Username + ":" + password));
            return new SoapAuthorization(new AuthenticationHeaderValue("Basic", parameter), password, basic.Username);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidDataException("The SOAP Basic authentication password file is missing, unsafe, or invalid.");
        }
    }

    private static async Task<byte[]> ReadLocalFileAsync(string path, int maximum, bool ownerOnly, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidDataException();
        path = Path.GetFullPath(path);
        var start = new ProcessStartInfo("/usr/bin/stat")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--printf=%u:%f\\n");
        start.ArgumentList.Add("--");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            start.ArgumentList.Add(current);
        using var process = Process.Start(start) ?? throw new InvalidDataException();
        using var cancellation = token.Register(static state =>
        {
            try { ((Process)state!).Kill(); }
            catch (InvalidOperationException) { }
        }, process);
        var metadataTask = process.StandardOutput.ReadToEndAsync(token);
        var errorsTask = process.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(metadataTask, errorsTask, process.WaitForExitAsync(token));
        var metadata = (await metadataTask).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (process.ExitCode != 0 || metadata.Length != start.ArgumentList.Count - 2) throw new InvalidDataException();
        // stat does not dereference links. Trust only a regular file and a root-owned, non-writable directory chain.
        for (var i = 0; i < metadata.Length; i++)
        {
            var item = metadata[i];
            if (!item.StartsWith("0:", StringComparison.Ordinal) || !int.TryParse(item.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mode)
                || (mode & 0xf000) != (i == 0 ? 0x8000 : 0x4000) || (mode & (i == 0 && ownerOnly ? 0x3f : 0x12)) != 0)
                throw new InvalidDataException();
        }
        await using var stream = File.OpenRead(path);
        return await SoapAgentXml.ReadBoundedAsync(stream, maximum, token);
    }

    internal static Uri RegisteredUrl(string? value, bool metadata)
    {
        if (value is null || value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !SafeUrl(uri, metadata))
            throw new InvalidDataException("The SOAP profile requires credential-free HTTP(S) URLs without fragments or arbitrary queries.");
        return uri;
    }

    internal static bool SafeUrl(Uri uri, bool metadata)
    {
        if (!uri.IsAbsoluteUri || uri.AbsoluteUri.Length > 2048 || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.OriginalString.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or '#'))
            return false;
        var remainder = uri.OriginalString.AsSpan(uri.Scheme.Length);
        if (!remainder.StartsWith("://", StringComparison.Ordinal)) return false;
        var authority = remainder[3..];
        var delimiter = authority.IndexOfAny('/', '?');
        if ((delimiter < 0 ? authority : authority[..delimiter]).Contains('@')) return false;
        return uri.Query.Length == 0 || metadata && IsMetadataQuery(uri.Query);
    }

    private static bool IsMetadataQuery(string query)
    {
        if (query is "?wsdl" or "?WSDL" or "?singleWsdl" or "?xsd") return true;
        foreach (var prefix in new[] { "?wsdl=wsdl", "?xsd=xsd", "?wsdl=", "?xsd=" })
            if (query.StartsWith(prefix, StringComparison.Ordinal) && query.Length > prefix.Length
                && query.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') == -1) return true;
        return false;
    }

    internal static bool SameOrigin(Uri root, Uri candidate) => root.Scheme == candidate.Scheme && root.Port == candidate.Port
        && string.Equals(root.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase);
}

internal sealed record SoapProfileFile(Dictionary<string, SoapServiceProfile?>? Services);
internal sealed record SoapServiceProfile
{
    public string Environment { get; init; } = "production";
    public string? Wsdl { get; init; }
    public string[] WsdlDependencies { get; init; } = [];
    public string? Endpoint { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public SoapBasicAuth? BasicAuth { get; init; }
    public string[] RedactNames { get; init; } = [];
    public SoapRedactionEntry?[]? RedactEntries { get; init; } = [];
    public Dictionary<string, SoapOperationProfile?>? Operations { get; init; } = new(StringComparer.Ordinal);
}
internal sealed record SoapBasicAuth(string Username, string PasswordFile);
internal sealed record SoapAuthorization(AuthenticationHeaderValue Header, string Password, string Username);
internal sealed record SoapOperationProfile(string Mode, string? Action, string? Template, Dictionary<string, SoapArgumentRule>? Arguments,
    SoapBusinessSuccess? BusinessSuccess = null, string? ResponseElement = null);
internal sealed record SoapRedactionEntry(string? Element, string? MatchChild, [property: JsonPropertyName("equals")] string? Expected = null);
internal sealed record SoapArgumentRule(string Type, int? MaxLength = null, string[]? AllowedValues = null, decimal? Min = null, decimal? Max = null);
internal sealed record SoapBusinessSuccess(string[] Path, [property: JsonPropertyName("equals")] string Expected);

internal sealed class SoapRequestTemplate
{
    private const int MaxStringLength = 4096;
    private const string MessageId = "dmsMessageId";
    private readonly XDocument _document;
    private readonly Dictionary<string, SoapArgumentRule> _rules;
    private readonly List<(XElement Element, string Name)> _placeholders = [];
    private readonly XName[]? _businessPath;
    private readonly string? _businessValue;
    private readonly XName? _responseName;

    internal SoapRequestTemplate(SoapOperationProfile operation, bool requireConfirmation)
    {
        if (operation.Action is null || operation.Action.Length > 1024 || operation.Action.Any(c => c < 0x20 || c > 0x7e || c is '"' or '\\')
            || operation.Template is null || Encoding.UTF8.GetByteCount(operation.Template) > SoapLimits.MaxArgsBytes
            || operation.Arguments is null || operation.Arguments.Count > 64)
            throw InvalidTemplate();
        try
        {
            _document = SoapAgentXml.Parse(operation.Template);
            (_, var body) = SoapAgentXml.Envelope(_document);
            if (body.Elements().Count() != 1 || body.Elements().Single().Name == body.Name.Namespace + "Fault")
                throw InvalidTemplate();
            _rules = operation.Arguments;
            foreach (var (name, rule) in _rules)
            {
                XmlConvert.VerifyNCName(name);
                if (name == MessageId || name.Length > 64 || rule is null || rule.Type is not ("string" or "integer" or "number" or "boolean")
                    || rule.MaxLength is < 1 or > MaxStringLength || rule.AllowedValues is { Length: > 128 or 0 }
                    || (rule.Type != "string" && rule.MaxLength is not null)
                    || (rule.Type is "string" or "boolean" && (rule.Min is not null || rule.Max is not null))
                    || (rule.Min is not null && rule.Max is not null && rule.Min > rule.Max))
                    throw InvalidTemplate();
            }
            foreach (var element in _document.Descendants())
            {
                if (element.Attributes().Any(a => HasMarker(a.Value))) throw InvalidTemplate();
                var text = string.Concat(element.Nodes().OfType<XText>().Select(n => n.Value));
                if (!HasMarker(text)) continue;
                if (element.HasElements || !text.StartsWith("{{", StringComparison.Ordinal) || !text.EndsWith("}}", StringComparison.Ordinal)
                    || text.Length < 5) throw InvalidTemplate();
                var name = text[2..^2];
                if (name != MessageId && !_rules.ContainsKey(name)) throw InvalidTemplate();
                _placeholders.Add((element, name));
            }
            if (!_placeholders.Where(p => p.Name != MessageId).Select(p => p.Name).ToHashSet(StringComparer.Ordinal).SetEquals(_rules.Keys))
                throw InvalidTemplate();
            if (requireConfirmation && (operation.ResponseElement is null || operation.BusinessSuccess is null))
                throw InvalidTemplate();
            if (operation.ResponseElement is { } responseElement)
            {
                _responseName = SoapAgentXml.ParseName(responseElement);
                if (_responseName == body.Name.Namespace + "Fault") throw InvalidTemplate();
            }
            if (operation.BusinessSuccess is { } business)
            {
                if (business.Path is not { Length: > 0 and <= 24 } || business.Expected is null || business.Expected.Length > 1024)
                    throw InvalidTemplate();
                _businessPath = business.Path.Select(SoapAgentXml.ParseName).ToArray();
                if (_responseName is not null && _businessPath[0] != _responseName) throw InvalidTemplate();
                _businessValue = business.Expected;
            }
        }
        catch (Exception e) when (e is XmlException or InvalidDataException or ArgumentException)
        {
            throw InvalidTemplate();
        }
        Action = operation.Action;
    }

    internal string Action { get; }
    internal XNamespace EnvelopeNamespace => _document.Root!.Name.Namespace;

    internal byte[] Bind(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw InvalidArguments();
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            foreach (var property in json.RootElement.EnumerateObject())
            {
                if (!_rules.TryGetValue(property.Name, out var rule) || !values.TryAdd(property.Name, BindValue(property.Value, rule)))
                    throw InvalidArguments();
            }
            if (values.Count != _rules.Count) throw InvalidArguments();
            values[MessageId] = $"urn:uuid:{Guid.NewGuid():D}";
            foreach (var (element, name) in _placeholders) element.Value = values[name];
            var request = SoapAgentXml.Utf8(_document);
            if (request.Length > SoapLimits.MaxXmlBytes) throw InvalidArguments();
            return request;
        }
        catch (Exception e) when (e is JsonException or XmlException or ArgumentException)
        {
            throw InvalidArguments();
        }
    }

    private static string BindValue(JsonElement element, SoapArgumentRule rule)
    {
        string value;
        switch (rule.Type)
        {
            case "string" when element.ValueKind == JsonValueKind.String:
                value = XmlConvert.VerifyXmlChars(element.GetString()!);
                if (value.Length > (rule.MaxLength ?? MaxStringLength)) throw InvalidArguments();
                break;
            case "integer" when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var integer):
                if (integer < rule.Min || integer > rule.Max) throw InvalidArguments();
                value = integer.ToString(CultureInfo.InvariantCulture);
                break;
            case "number" when element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var number):
                if (number < rule.Min || number > rule.Max) throw InvalidArguments();
                value = number.ToString("0.############################", CultureInfo.InvariantCulture);
                break;
            case "boolean" when element.ValueKind is JsonValueKind.True or JsonValueKind.False:
                value = element.GetBoolean() ? "true" : "false";
                break;
            default: throw InvalidArguments();
        }
        if (rule.AllowedValues is not null && !rule.AllowedValues.Contains(value, StringComparer.Ordinal)) throw InvalidArguments();
        return value;
    }

    internal string ClassifyResponse(XElement body, XElement response, bool requireConfirmation)
    {
        if (_responseName is not null && response.Name != _responseName) return "unexpected-response";
        if (_businessPath is null) return "success";
        var element = body;
        foreach (var name in _businessPath)
        {
            var matches = element.Elements(name).Take(2).ToArray();
            if (matches.Length != 1) return requireConfirmation ? "unconfirmed-response" : "business-failure";
            element = matches[0];
            if (SoapAgentXml.IsNil(element)) return requireConfirmation ? "unconfirmed-response" : "business-failure";
        }
        if (element.HasElements) return requireConfirmation ? "unconfirmed-response" : "business-failure";
        return element.Value == _businessValue ? "success" : "business-failure";
    }

    private static bool HasMarker(string text) => text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal);
    private static InvalidDataException InvalidTemplate() => new("The registered SOAP operation template or argument rules are invalid.");
    private static InvalidDataException InvalidArguments() => new("The SOAP argument artifact does not satisfy the registered operation rules.");
}
