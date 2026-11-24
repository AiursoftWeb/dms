using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows;

internal static class SoapAgentXml
{
    internal static readonly XNamespace Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    internal static readonly XNamespace Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace Wsdl11 = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Wsdl20 = "http://www.w3.org/ns/wsdl";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";
    private const string Redacted = "[redacted]";
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "passwd", "pwd", "passworddigest", "username", "secret", "clientsecret", "sharedsecret",
        "credential", "credentials", "authorization", "proxyauthorization", "security", "usernametoken", "securitytoken",
        "token", "accesstoken", "authtoken", "refreshtoken", "sessiontoken", "idtoken", "binarysecuritytoken",
        "apikey", "key", "privatekey", "sessionid", "sessionkey", "sessionsecret", "cookie", "setcookie"
    };

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        MaxCharactersInDocument = SoapLimits.MaxXmlBytes
    };

    internal static XDocument Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using (var reader = XmlReader.Create(stream, ReaderSettings())) CheckStructure(reader);
        stream.Position = 0;
        using var document = XmlReader.Create(stream, ReaderSettings());
        return XDocument.Load(document, LoadOptions.PreserveWhitespace);
    }

    internal static XDocument Parse(string text)
    {
        using (var source = new StringReader(text))
        using (var reader = XmlReader.Create(source, ReaderSettings())) CheckStructure(reader);
        using var documentSource = new StringReader(text);
        using var document = XmlReader.Create(documentSource, ReaderSettings());
        return XDocument.Load(document, LoadOptions.PreserveWhitespace);
    }

    private static void CheckStructure(XmlReader reader)
    {
        // Bound nesting and node count before constructing a tree from untrusted XML.
        var nodes = 0;
        while (reader.Read())
            if (reader.Depth > 64 || (nodes += 1 + reader.AttributeCount) > 150_000)
                throw new InvalidDataException("SOAP XML exceeds the nesting or node limit.");
    }

    internal static byte[] Utf8(XDocument document)
    {
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize }))
            document.Save(writer);
        return output.ToArray();
    }

    internal static (XElement? Header, XElement Body) Envelope(XDocument document)
    {
        var root = document.Root;
        if (root is null || (root.Name != Soap11 + "Envelope" && root.Name != Soap12 + "Envelope")) throw InvalidEnvelope();
        var ns = root.Name.Namespace;
        var elements = root.Elements().Take(3).ToArray();
        if (HasMixedText(root)) throw InvalidEnvelope();
        var (header, body) = elements switch
        {
            [var b] when b.Name == ns + "Body" => ((XElement?)null, b),
            [var h, var b] when h.Name == ns + "Header" && b.Name == ns + "Body" => (h, b),
            _ => throw InvalidEnvelope()
        };
        if (HasMixedText(body) || (header is not null && HasMixedText(header))) throw InvalidEnvelope();
        return (header, body);
    }

    private static bool HasMixedText(XElement element) => element.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value));
    private static InvalidDataException InvalidEnvelope() => new("Expected a SOAP 1.1 or SOAP 1.2 envelope.");

    internal static bool IsWsdl(XDocument document) => document.Root?.Name is { } name
        && (name == Wsdl11 + "definitions" || name == Wsdl20 + "description");

    internal static IEnumerable<string> MetadataReferences(XDocument document)
    {
        var root = document.Root;
        if (root is null || (root.Name != Wsdl11 + "definitions" && root.Name != Wsdl20 + "description" && root.Name != Xsd + "schema"))
            throw new InvalidDataException("A registered contract document is not WSDL or XML Schema.");
        if (document.Descendants().Any(e => e.Attribute(XNamespace.Xml + "base") is not null))
            throw new InvalidDataException("Registered SOAP metadata must not use xml:base.");
        foreach (var element in document.Descendants())
        {
            var parent = element.Parent?.Name;
            var name = element.Name;
            if ((parent == Wsdl11 + "definitions" && name == Wsdl11 + "import")
                || (parent == Wsdl20 + "description" && (name == Wsdl20 + "import" || name == Wsdl20 + "include")))
            {
                var location = (string?)element.Attribute("location");
                if (location is null && name == Wsdl20 + "import") continue;
                yield return RequireLocation(location);
            }
            else if (parent == Xsd + "schema" && (name == Xsd + "import" || name == Xsd + "include" || name == Xsd + "redefine" || name == Xsd + "override"))
            {
                var location = (string?)element.Attribute("schemaLocation");
                if (location is null && name == Xsd + "import") continue;
                yield return RequireLocation(location);
            }
        }
    }

    private static string RequireLocation(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 2048
        ? value.Trim() : throw new InvalidDataException("A SOAP metadata dependency has an invalid location.");

    internal static XName ParseName(string? value)
    {
        if (value is not { Length: > 0 and <= 1024 }) throw new XmlException();
        if (!value.StartsWith('{')) return XName.Get(XmlConvert.VerifyNCName(value));
        var close = value.IndexOf('}');
        if (close <= 1 || close == value.Length - 1) throw new XmlException();
        var ns = value[1..close];
        if (ns.Contains('{') || ns.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) throw new XmlException();
        return XName.Get(XmlConvert.VerifyNCName(value[(close + 1)..]), ns);
    }

    internal static bool IsNil(XElement element) =>
        ((string?)element.Attribute(XName.Get("nil", "http://www.w3.org/2001/XMLSchema-instance")))?.Trim() is "true" or "1";

    internal static SoapRedactionPolicy RedactionPolicy(string[]? extra, SoapRedactionEntry?[]? entries)
    {
        if (extra is null || extra.Length > 128 || entries is null || entries.Length > 32)
            throw new InvalidDataException("The Agent SOAP redaction configuration is invalid.");
        var names = new HashSet<string>(SensitiveNames, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in extra)
            {
                if (string.IsNullOrEmpty(name) || name.Length > 128 || NormalizeName(name).Length == 0) throw new XmlException();
                names.Add(NormalizeName(XmlConvert.VerifyNCName(name)));
            }
            var rules = new List<SoapRedactionRule>();
            foreach (var entry in entries)
            {
                if (entry is null || entry.Expected is { Length: > 1024 }) throw new XmlException();
                rules.Add(new(ParseName(entry.Element), ParseName(entry.MatchChild), entry.Expected));
            }
            return new(names, rules);
        }
        catch (XmlException) { throw new InvalidDataException("The Agent SOAP redaction configuration is invalid."); }
    }

    internal static byte[]? Redact(XDocument document, XElement? header, bool fault, SoapRedactionPolicy policy, SoapAuthorization? authorization)
    {
        if (fault)
        {
            // A fault can embed arbitrary server data. Publish only a sentinel SOAP fault, not its payload or diagnostics.
            var ns = document.Root!.Name.Namespace;
            var faultName = ns + "Fault";
            return Utf8(new XDocument(new XElement(ns + "Envelope", new XAttribute(XNamespace.Xmlns + "s", ns.NamespaceName),
                new XElement(ns + "Body", ns == Soap11
                    ? new XElement(faultName, new XElement("faultcode", "s:Server"), new XElement("faultstring", Redacted))
                    : new XElement(faultName, new XElement(ns + "Code", new XElement(ns + "Value", "s:Receiver")),
                        new XElement(ns + "Reason", new XElement(ns + "Text", new XAttribute(XNamespace.Xml + "lang", "en"), Redacted)))))));
        }
        header?.Remove();
        if (policy.Entries.Count > 0)
            document.Descendants().Where(element => policy.Entries.Any(rule => MatchesEntry(element, rule))).Remove();
        var names = policy.Names;
        var credentials = authorization is null ? Array.Empty<string>() : new[]
        {
            authorization.Password, authorization.Username, authorization.Header.Parameter!
        };
        foreach (var element in document.Descendants().ToArray())
        {
            if (names.Contains(NormalizeName(element.Name.LocalName)))
            {
                element.Attributes().Where(a => !a.IsNamespaceDeclaration).Remove();
                element.Value = Redacted;
                continue;
            }
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            {
                if (names.Contains(NormalizeName(attribute.Name.LocalName)) || ContainsCredential(attribute.Value)) attribute.Value = Redacted;
            }
            var text = element.Nodes().OfType<XText>().ToArray();
            if (credentials.Length > 0 && ContainsCredential(string.Concat(text.Select(t => t.Value))))
                foreach (var item in text) item.Value = Redacted;
        }
        // Names and namespace URIs cannot generally be rewritten without changing the contract.
        if (credentials.Length > 0 && document.Descendants().Any(e => ContainsCredential(e.Name.LocalName)
            || ContainsCredential(e.Name.NamespaceName) || e.Attributes().Any(a => ContainsCredential(a.Name.LocalName)
                || ContainsCredential(a.Name.NamespaceName) || a.IsNamespaceDeclaration && ContainsCredential(a.Value)))) return null;
        var bytes = Utf8(document);
        return bytes.Length <= SoapLimits.MaxXmlBytes ? bytes : null;

        bool ContainsCredential(string value) => credentials.Any(x => value.Contains(x, StringComparison.Ordinal));

        bool MatchesEntry(XElement element, SoapRedactionRule rule) => MatchesName(element.Name, rule.Element)
            && element.Elements().Any(child => MatchesName(child.Name, rule.MatchChild) && MatchesValue(child.Value.Trim(), rule.Expected));

        bool MatchesValue(string value, string? expected) => expected is null
            ? policy.Names.Contains(NormalizeName(value)) : value.Equals(expected, StringComparison.Ordinal);
    }

    private static bool MatchesName(XName actual, XName configured) => configured.Namespace == XNamespace.None
        ? actual.LocalName.Equals(configured.LocalName, StringComparison.OrdinalIgnoreCase) : actual == configured;

    private static string NormalizeName(string name) => name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum - (int)output.Length + 1)), token)) != 0)
        {
            if (output.Length + read > maximum) throw new SoapPayloadTooLargeException();
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

internal sealed record SoapRedactionPolicy(HashSet<string> Names, IReadOnlyList<SoapRedactionRule> Entries);
internal sealed record SoapRedactionRule(XName Element, XName MatchChild, string? Expected);
internal sealed class SoapPayloadTooLargeException : IOException;
