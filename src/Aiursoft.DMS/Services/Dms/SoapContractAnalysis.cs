using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Services.Dms;

public sealed record SoapInspectionResult(IReadOnlyList<SoapOperation> Operations, IReadOnlyList<string> Warnings);
public sealed record SoapOperation(string PortType, string Name, SoapMessage? Request, SoapMessage? Response);
public sealed record SoapMessage(string Name, IReadOnlyList<SoapField> Fields);
public sealed record SoapField(string Name, string Type, string Kind, bool Required, string MinOccurs, string MaxOccurs,
    bool Nillable, IReadOnlyList<SoapField> Fields, string? Part = null, string? BaseType = null);
public sealed record SoapDiffResult(IReadOnlyList<SoapContractChange> Changes, IReadOnlyList<string> Warnings);
public sealed record SoapContractChange(string Kind, string PortType, string Operation, string? Direction,
    IReadOnlyList<string> Path, string? Before, string? After);
public sealed record XmlValidationResult(bool Valid, IReadOnlyList<XmlValidationDiagnostic> Errors, bool Truncated = false);
public sealed record XmlValidationDiagnostic(string Code, string Message, int Line, int Column);

public static class SoapContractAnalysis
{
    private static readonly XNamespace Wsdl = "http://schemas.xmlsoap.org/wsdl/";
    private static readonly XNamespace Xsd = XmlSchema.Namespace;
    private static readonly Uri RelativeBase = new("https://dms-contract.invalid/");
    private const int MaxDepth = 64;
    private const int MaxContractNodes = 40_000;
    private const int MaxSchemaNodes = 2_048;
    private const int MaxSchemaSources = 48;
    private const int MaxSchemaLinks = 96;
    private const int MaxInstanceNodes = 100_000;
    private const int MaxShapeDepth = 24;
    private const int MaxShapeItems = 4_096;
    // ClusterWorkflowRuntime caps the serialized result at 32,768 characters.
    private const int MaxResultBytes = 32 * 1024;
    private const int MaxDiagnostics = 32;
    private const string Scope = "Inspection covers WSDL 1.1 portType operations and XSD message field shapes, not bindings, endpoints, headers or faults.";

    public static SoapInspectionResult Inspect(SoapContractBundle bundle)
    {
        var contract = new Contract(bundle);
        if (contract.Root.Element.Name != Wsdl + "definitions")
            throw new InvalidDataException("Inspection requires a WSDL 1.1 definitions document as the bundle root.");
        try
        {
            return Bounded(new Inspector(contract.BuildSchemas()).Read(contract.Wsdls()));
        }
        catch (XmlSchemaException)
        {
            throw new InvalidDataException("The contract contains an invalid XSD schema.");
        }
    }

    public static SoapDiffResult Diff(SoapContractBundle before, SoapContractBundle after)
    {
        var oldContract = Inspect(before);
        var newContract = Inspect(after);
        var changes = new List<SoapContractChange>();
        var oldOperations = oldContract.Operations.ToDictionary(operation => (operation.PortType, operation.Name));
        var newOperations = newContract.Operations.ToDictionary(operation => (operation.PortType, operation.Name));
        foreach (var key in oldOperations.Keys.Union(newOperations.Keys)
                     .OrderBy(key => key.PortType, StringComparer.Ordinal).ThenBy(key => key.Name, StringComparer.Ordinal))
        {
            if (!oldOperations.TryGetValue(key, out var oldOperation)) Add("operation-added", null, [], null, key.Name);
            else if (!newOperations.TryGetValue(key, out var newOperation)) Add("operation-removed", null, [], key.Name, null);
            else
            {
                CompareMessage("request", oldOperation.Request, newOperation.Request);
                CompareMessage("response", oldOperation.Response, newOperation.Response);
            }

            void Add(string kind, string? direction, IReadOnlyList<string> path, string? oldValue, string? newValue)
            {
                if (changes.Count >= MaxShapeItems)
                    throw new InvalidDataException("The WSDL diff exceeds the change limit.");
                changes.Add(new(kind, key.PortType, key.Name, direction, path, oldValue, newValue));
            }

            void CompareMessage(string direction, SoapMessage? oldMessage, SoapMessage? newMessage)
            {
                if (oldMessage is null || newMessage is null)
                {
                    if (oldMessage is not null || newMessage is not null)
                        Add(oldMessage is null ? "message-added" : "message-removed", direction, [], oldMessage?.Name, newMessage?.Name);
                    return;
                }
                if (oldMessage.Name != newMessage.Name)
                    Add("message-changed", direction, [], oldMessage.Name, newMessage.Name);
                CompareFields(direction, [], oldMessage.Fields, newMessage.Fields);
            }

            void CompareFields(string direction, string[] path, IReadOnlyList<SoapField> oldFields, IReadOnlyList<SoapField> newFields)
            {
                // Repeated field names are legal in separate positions of a sequence. Match their occurrences in order.
                static (string Kind, string Name) Key(SoapField field) => (field.Kind, field.Part ?? field.Name);
                var oldGroups = oldFields.ToLookup(Key);
                var newGroups = newFields.ToLookup(Key);
                var sharedCounts = oldGroups.ToDictionary(group => group.Key,
                    group => Math.Min(group.Count(), newGroups[group.Key].Count()));
                if (!SharedOrder(oldFields).SequenceEqual(SharedOrder(newFields)))
                    Add("field-order-changed", direction, path, null, null);

                IEnumerable<(string Kind, string Name)> SharedOrder(IReadOnlyList<SoapField> fields)
                {
                    var seen = new Dictionary<(string Kind, string Name), int>();
                    foreach (var field in fields)
                    {
                        var fieldKey = Key(field);
                        var count = seen.GetValueOrDefault(fieldKey) + 1;
                        seen[fieldKey] = count;
                        if (count <= sharedCounts.GetValueOrDefault(fieldKey)) yield return fieldKey;
                    }
                }
                foreach (var fieldKey in oldGroups.Select(group => group.Key).Union(newGroups.Select(group => group.Key)))
                {
                    var oldGroup = oldGroups[fieldKey].ToArray();
                    var newGroup = newGroups[fieldKey].ToArray();
                    var count = Math.Max(oldGroup.Length, newGroup.Length);
                    for (var i = 0; i < count; i++)
                    {
                        var label = fieldKey.Kind == "attribute" ? "@" + fieldKey.Name : fieldKey.Name;
                        string[] fieldPath = [.. path, count == 1 ? label : $"{label}[{i + 1}]"];
                        if (i >= oldGroup.Length) Add("field-added", direction, fieldPath, null, newGroup[i].Type);
                        else if (i >= newGroup.Length) Add("field-removed", direction, fieldPath, oldGroup[i].Type, null);
                        else
                        {
                            var oldField = oldGroup[i];
                            var newField = newGroup[i];
                            Property("field-name-changed", oldField.Name, newField.Name);
                            Property("field-type-changed", oldField.Type, newField.Type);
                            Property("field-base-type-changed", oldField.BaseType, newField.BaseType);
                            Property("field-required-changed", Boolean(oldField.Required), Boolean(newField.Required));
                            Property("field-min-occurs-changed", oldField.MinOccurs, newField.MinOccurs);
                            Property("field-max-occurs-changed", oldField.MaxOccurs, newField.MaxOccurs);
                            Property("field-nillable-changed", Boolean(oldField.Nillable), Boolean(newField.Nillable));
                            CompareFields(direction, fieldPath, oldField.Fields, newField.Fields);
                        }

                        void Property(string kind, string? oldValue, string? newValue)
                        {
                            if (oldValue != newValue) Add(kind, direction, fieldPath, oldValue, newValue);
                        }
                    }
                }
            }
        }
        return Bounded(new SoapDiffResult(changes, oldContract.Warnings.Union(newContract.Warnings).Order(StringComparer.Ordinal).ToArray()));
    }

    public static XmlValidationResult Validate(XDocument xml, SoapContractBundle schemas)
    {
        XmlSchemaSet schemaSet;
        try
        {
            schemaSet = new Contract(schemas).BuildSchemas();
            if (schemaSet.Count == 0) return Failure("invalid-schema", "The bundle contains no XSD schemas.");
        }
        catch (XmlSchemaException ex) { return Failure("invalid-schema", "The supplied XSD is invalid.", ex.LineNumber, ex.LinePosition); }
        catch (InvalidDataException ex) { return Failure("invalid-schema", ex.Message); }

        if (xml.DocumentType is not null) return Failure("invalid-xml", "Document type declarations are not allowed.");
        if (xml.Root is not { } root) return Failure("invalid-xml", "The XML has no root element.");
        try
        {
            using var reader = xml.CreateReader();
            var nodes = MaxInstanceNodes;
            CheckTree(reader, ref nodes);
            // Count the encoded XML without building a large intermediate string or buffer.
            using var stream = new LimitedSink(SoapLimits.MaxXmlBytes);
            using var writer = XmlWriter.Create(stream, new XmlWriterSettings { OmitXmlDeclaration = true });
            xml.WriteTo(writer);
        }
        catch (InvalidDataException ex) { return Failure("invalid-xml", ex.Message); }
        catch (XmlException ex) { return Failure("invalid-xml", "The XML is malformed.", ex.LineNumber, ex.LinePosition); }
        catch (ArgumentException) { return Failure("invalid-xml", "The XML is malformed."); }

        if (!schemaSet.GlobalElements.Contains(new XmlQualifiedName(root.Name.LocalName, root.Name.NamespaceName)))
        {
            var (line, column) = Location(root);
            return Failure("unknown-root", "The XML root has no global declaration in the supplied schema.", line, column);
        }
        var errors = new List<XmlValidationDiagnostic>();
        var priorCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            ValidateInstance(root, schemaSet, errors);
        }
        catch (DiagnosticLimitException) { return new(false, errors, Truncated: true); }
        catch (XmlSchemaException ex)
        {
            return Failure("validation-failed", "The XML does not satisfy the supplied XSD.", ex.LineNumber, ex.LinePosition);
        }
        finally { CultureInfo.CurrentUICulture = priorCulture; }
        return new(errors.Count == 0, errors);
    }

    private enum ValidationPhase { Structure, Attribute, EndAttributes, EndElement, IdentityConstraints }

    private static void ValidateInstance(XElement root, XmlSchemaSet schemas, List<XmlValidationDiagnostic> errors)
    {
        XNamespace xsi = XmlSchema.InstanceNamespace;
        var namespaces = new XmlNamespaceManager(schemas.NameTable);
        var validator = new XmlSchemaValidator(schemas.NameTable, schemas, namespaces,
            XmlSchemaValidationFlags.AllowXmlAttributes | XmlSchemaValidationFlags.ProcessIdentityConstraints) { XmlResolver = null };
        var phase = ValidationPhase.Structure;
        validator.ValidationEventHandler += (_, args) =>
        {
            if (errors.Count == MaxDiagnostics) throw new DiagnosticLimitException();
            var exception = args.Exception;
            var (code, message) = Classify(phase, exception);
            errors.Add(new(code, message, exception.LineNumber, exception.LinePosition));
        };
        validator.Initialize();
        Visit(root);
        phase = ValidationPhase.IdentityConstraints;
        validator.EndValidation();

        void Visit(XElement element)
        {
            namespaces.PushScope();
            foreach (var attribute in element.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                namespaces.AddNamespace(attribute.Name.Namespace == XNamespace.None ? "" : attribute.Name.LocalName, attribute.Value);

            phase = ValidationPhase.Structure;
            validator.LineInfoProvider = element;
            validator.ValidateElement(element.Name.LocalName, element.Name.NamespaceName, null,
                (string?)element.Attribute(xsi + "type"), (string?)element.Attribute(xsi + "nil"), null, null);
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            {
                phase = ValidationPhase.Attribute;
                validator.LineInfoProvider = attribute;
                validator.ValidateAttribute(attribute.Name.LocalName, attribute.Name.NamespaceName, attribute.Value, null);
            }
            phase = ValidationPhase.EndAttributes;
            validator.LineInfoProvider = element;
            validator.ValidateEndOfAttributes(null);
            foreach (var node in element.Nodes())
            {
                if (node is XElement child) Visit(child);
                else if (node is XText { Value.Length: > 0 } text)
                {
                    phase = ValidationPhase.Structure;
                    validator.LineInfoProvider = text;
                    validator.ValidateText(text.Value);
                }
            }
            phase = ValidationPhase.EndElement;
            validator.LineInfoProvider = element;
            validator.ValidateEndElement(null);
            namespaces.PopScope();
        }
    }

    private static (string Code, string Message) Classify(ValidationPhase phase, XmlSchemaException exception)
    {
        if (phase == ValidationPhase.EndAttributes)
            return ("required-attribute", "A required attribute is missing.");
        if (exception.InnerException is not null && phase is ValidationPhase.Attribute or ValidationPhase.EndElement)
            return ("type-mismatch", "The value does not satisfy its XSD type or facets.");
        // System.Xml exposes no code for incomplete content. Match its invariant diagnostic only in the end-element phase.
        // Do not emit the framework exception: it may contain instance values even in a structural error.
        if (phase == ValidationPhase.EndElement && exception.Message.StartsWith("The element ", StringComparison.Ordinal) &&
            exception.Message.Contains(" has incomplete content", StringComparison.Ordinal))
            return ("incomplete-content", "Required element content is missing or incomplete.");
        if (phase == ValidationPhase.Structure || phase == ValidationPhase.Attribute)
            return ("invalid-structure", "An element, attribute or text node is not allowed at this location.");
        return ("validation-failed", "The XML does not satisfy the supplied XSD at this location.");
    }

    private static string Boolean(bool value) => value ? "true" : "false";

    private static T Bounded<T>(T result)
    {
        using var stream = new LimitedSink(MaxResultBytes, "The SOAP analysis result exceeds the size limit.");
        JsonSerializer.Serialize(stream, result, new JsonSerializerOptions { MaxDepth = MaxShapeDepth * 3 });
        return result;
    }

    private static XmlValidationResult Failure(string code, string message, int line = 0, int column = 0)
        => new(false, [new(code, message, line, column)]);

    private static (int Line, int Column) Location(object? source) => source is IXmlLineInfo { } info && info.HasLineInfo()
        ? (info.LineNumber, info.LinePosition) : (0, 0);

    private static string Name(XmlQualifiedName name) => Name(name.Namespace, name.Name);
    private static string Name(XName name) => Name(name.NamespaceName, name.LocalName);

    private static string Name(string ns, string name)
    {
        if (ns.Length + name.Length > 512) throw new InvalidDataException("A contract name exceeds the length limit.");
        return ns.Length == 0 ? name : $"{{{ns}}}{name}";
    }

    private static string NcName(XElement element, string attribute)
    {
        if ((string?)element.Attribute(attribute) is not { Length: > 0 } value || value.Length > 512)
            throw new InvalidDataException("A required WSDL name is missing or too long.");
        try { return XmlConvert.VerifyNCName(value.Trim()); }
        catch (XmlException) { throw new InvalidDataException("The WSDL contains an invalid name."); }
    }

    private static XName QName(XElement element, XAttribute attribute) => QName(element, attribute.Value);

    private static XName QName(XElement element, string reference)
    {
        var value = reference.Trim();
        if (value.Length > 512) throw new InvalidDataException("A WSDL reference exceeds the length limit.");
        var delimiter = value.IndexOf(':');
        var name = delimiter < 0 ? value : value[(delimiter + 1)..];
        XNamespace? ns;
        try
        {
            XmlConvert.VerifyNCName(name);
            ns = delimiter < 0 ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(XmlConvert.VerifyNCName(value[..delimiter]));
        }
        catch (XmlException) { throw new InvalidDataException("The WSDL contains an invalid qualified reference."); }
        if (ns is null) throw new InvalidDataException("A WSDL reference uses an undeclared namespace prefix.");
        return ns + name;
    }

    private static XmlQualifiedName SchemaName(XName name) => new(name.LocalName, name.NamespaceName);

    private static void CheckTree(XmlReader reader, ref int remaining)
    {
        while (reader.Read())
        {
            if (reader.Depth > MaxDepth) throw new InvalidDataException("The XML exceeds the nesting limit.");
            remaining -= 1 + reader.AttributeCount;
            if (remaining < 0) throw new InvalidDataException("The XML exceeds the structural work limit.");
        }
    }

    private sealed class DiagnosticLimitException : Exception;

    private sealed class LimitedSink(int limit, string error = "The XML exceeds the byte limit.") : Stream
    {
        private int written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > limit - written) throw new InvalidDataException(error);
            written += buffer.Length;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed record ContractDocument(Uri Uri, XElement Element);
    private sealed record SchemaSource(ContractDocument Document, XElement Element, XmlSchema Schema);

    private sealed class Contract
    {
        private static readonly XmlReaderSettings Settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = SoapLimits.MaxContractBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        private readonly Dictionary<string, ContractDocument> documents = new(StringComparer.Ordinal);
        private List<ContractDocument>? wsdls;
        public ContractDocument Root { get; }

        public Contract(SoapContractBundle bundle)
        {
            if (bundle is null || bundle.Documents is null || bundle.Documents.Count is 0 or > SoapLimits.MaxDocuments)
                throw new InvalidDataException("The SOAP contract document count is out of bounds.");
            var rootUri = ResourceUri(RelativeBase, bundle.Root);
            var bytes = SoapLimits.MaxContractBytes;
            var nodes = MaxContractNodes;
            foreach (var (id, text) in bundle.Documents)
            {
                var uri = ResourceUri(RelativeBase, id);
                if (text is null || text.Length > bytes || (bytes -= Encoding.UTF8.GetByteCount(text)) < 0)
                    throw new InvalidDataException("The SOAP contract exceeds the byte limit.");
                try
                {
                    using (var count = XmlReader.Create(new StringReader(text), Settings)) CheckTree(count, ref nodes);
                    using var reader = XmlReader.Create(new StringReader(text), Settings, uri.AbsoluteUri);
                    var element = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri).Root!;
                    if (element.DescendantsAndSelf().Any(item => item.Attribute(XNamespace.Xml + "base") is not null))
                        throw new InvalidDataException("xml:base is not supported in offline SOAP contracts.");
                    if (!documents.TryAdd(uri.AbsoluteUri, new(uri, element)))
                        throw new InvalidDataException("The SOAP contract contains duplicate document identifiers.");
                }
                catch (XmlException ex)
                {
                    throw new InvalidDataException($"Invalid contract XML at line {ex.LineNumber}, column {ex.LinePosition}. DTDs are not allowed.");
                }
            }
            if (!documents.TryGetValue(rootUri.AbsoluteUri, out var root))
                throw new InvalidDataException("The SOAP contract root is not present in the bundle.");
            Root = root;
        }

        public IReadOnlyList<ContractDocument> Wsdls()
        {
            if (wsdls is not null) return wsdls;
            wsdls = [];
            if (Root.Element.Name != Wsdl + "definitions") return wsdls;
            var visited = new HashSet<ContractDocument> { Root };
            wsdls.Add(Root);
            var remaining = MaxSchemaLinks;
            for (var i = 0; i < wsdls.Count; i++)
            {
                var document = wsdls[i];
                foreach (var import in document.Element.Elements(Wsdl + "import"))
                {
                    if (--remaining < 0) throw new InvalidDataException("The WSDL exceeds the import limit.");
                    var target = Resolve(document, (string?)import.Attribute("location"));
                    if (target.Element.Name != Wsdl + "definitions" ||
                        (string?)import.Attribute("namespace") is not { } ns || ns != TargetNamespace(target.Element))
                        throw new InvalidDataException("A WSDL import does not identify matching WSDL 1.1 definitions.");
                    if (visited.Add(target)) wsdls.Add(target);
                }
            }
            return wsdls;
        }

        public XmlSchemaSet BuildSchemas()
        {
            if (Root.Element.Name != Wsdl + "definitions" && Root.Element.Name != Xsd + "schema")
                throw new InvalidDataException("The bundle root must be a WSDL 1.1 definitions or XSD schema document.");
            var standalone = documents.Values.Where(document => document.Element.Name == Xsd + "schema").ToArray();
            var definitions = Wsdls().SelectMany(document => document.Element.Elements(Wsdl + "types")
                .SelectMany(types => types.Elements(Xsd + "schema")).Select(element => (document, element)))
                .Concat(standalone.Select(document => (document, element: document.Element))).Take(MaxSchemaSources + 1).ToArray();
            if (definitions.Length > MaxSchemaSources) throw new InvalidDataException("The contract exceeds the XSD schema count limit.");
            var schemaSet = new XmlSchemaSet { XmlResolver = null };
            if (definitions.Length == 0)
            {
                if (Root.Element.Name == Xsd + "schema") throw new InvalidDataException("The bundle has no XSD schemas.");
                return schemaSet; // WSDL messages may use only built-in types or have no parts.
            }
            var schemaNodes = definitions.SelectMany(item => item.element.DescendantsAndSelf()).Take(MaxSchemaNodes + 1).ToArray();
            if (schemaNodes.Length > MaxSchemaNodes) throw new InvalidDataException("The contract exceeds the XSD structural work limit.");
            CheckSchemaWork(schemaNodes);
            var sources = definitions.Select(item =>
            {
                using var reader = item.element.CreateReader();
                return new SchemaSource(item.document, item.element, XmlSchema.Read(reader, RejectSchemaDiagnostic)!);
            }).ToArray();
            var byDocument = sources.Where(source => source.Document.Element == source.Element).ToDictionary(source => source.Document);
            var namespaces = sources.Select(source => source.Schema.TargetNamespace ?? "").ToHashSet(StringComparer.Ordinal);
            var included = new HashSet<XmlSchema>();
            var remaining = MaxSchemaLinks;
            foreach (var source in sources)
            {
                foreach (XmlSchemaExternal external in source.Schema.Includes)
                {
                    if (--remaining < 0) throw new InvalidDataException("The XSD exceeds the import and include limit.");
                    if (external.SchemaLocation is not { } location && external is XmlSchemaImport import)
                    {
                        if (!namespaces.Contains(import.Namespace ?? "") && import.Namespace != XNamespace.Xml.NamespaceName)
                            throw new InvalidDataException("An imported XSD namespace is not present in the bundle.");
                        continue;
                    }
                    var target = Resolve(source.Document, external.SchemaLocation);
                    if (!byDocument.TryGetValue(target, out var targetSchema))
                        throw new InvalidDataException("An XSD dependency does not identify a standalone schema in the bundle.");
                    // Attach pre-parsed schemas directly. The compiler never gets a resolver or any external resource access.
                    external.Schema = targetSchema.Schema;
                    if (external is not XmlSchemaImport) included.Add(targetSchema.Schema);
                }
            }
            var entrypoints = sources.Where(source => source.Element == Root.Element || source.Element != source.Document.Element ||
                !included.Contains(source.Schema)).ToArray();
            var reached = new HashSet<XmlSchema>();
            foreach (var entry in entrypoints) LinkReachable(entry.Schema);
            if (reached.Count != sources.Length)
                throw new InvalidDataException("The bundle contains an XSD include cycle with no entry point.");

            schemaSet.ValidationEventHandler += RejectSchemaDiagnostic;
            foreach (var source in entrypoints) schemaSet.Add(source.Schema);
            schemaSet.Compile();
            return schemaSet;

            void LinkReachable(XmlSchema schema)
            {
                if (!reached.Add(schema)) return;
                foreach (XmlSchemaExternal dependency in schema.Includes)
                    if (dependency.Schema is { } linked) LinkReachable(linked);
            }
        }

        private ContractDocument Resolve(ContractDocument source, string? location)
        {
            var uri = ResourceUri(source.Uri, location);
            return documents.TryGetValue(uri.AbsoluteUri, out var document) ? document
                : throw new InvalidDataException("A required WSDL or XSD dependency is not present in the bundle.");
        }

        private static Uri ResourceUri(Uri baseUri, string? location)
        {
            if (location is not { Length: > 0 and <= 2048 } || string.IsNullOrWhiteSpace(location) ||
                location.Contains('\\') || location.Any(char.IsControl) || !Uri.TryCreate(baseUri, location, out var uri) ||
                uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidDataException("The contract contains an invalid or unsupported document location.");
            return uri;
        }

        private static void RejectSchemaDiagnostic(object? sender, ValidationEventArgs args) => throw args.Exception;
    }

    private static string TargetNamespace(XElement element) => (string?)element.Attribute("targetNamespace") ?? "";

    private static void CheckSchemaWork(XElement[] nodes)
    {
        // System.Xml evaluates XSD pattern facets with the backtracking regex engine. Accept only a bounded subset.
        foreach (var pattern in nodes.Where(node => node.Name == Xsd + "pattern"))
            if (!BoundedPattern((string?)pattern.Attribute("value")))
                throw new InvalidDataException("An XSD pattern exceeds the supported regular expression complexity.");
        if (nodes.Count(node => node.Name == Xsd + "pattern") > 32)
            throw new InvalidDataException("The XSD exceeds the pattern limit.");

        // Bound work for the compiler's expansion of groups, derived types and nested unions. Element type references
        // are not expanded here: they may be recursive, and inspection limits field traversal separately.
        var declarations = nodes.Where(node => node.Parent?.Name == Xsd + "schema").ToArray();
        var globals = declarations.Where(node => (IsGroup(node) || IsType(node)) && node.Attribute("name") is not null)
            .ToLookup(node => (node.Name, NcName(node, "name")));
        var costs = new Dictionary<XElement, int>();
        var active = new HashSet<XElement>();
        var budget = MaxSchemaNodes * 4;
        foreach (var node in declarations)
        {
            budget -= Cost(node, 0);
            if (budget < 0) throw new InvalidDataException("The XSD exceeds the schema expansion limit.");
        }

        int Cost(XElement node, int depth)
        {
            if (costs.TryGetValue(node, out var known)) return known;
            if (depth >= MaxDepth || !active.Add(node)) throw new InvalidDataException("The XSD contains cyclic or deeply nested schema references.");
            var cost = 1;
            foreach (var child in node.Descendants())
            {
                cost++;
                if (IsGroup(child) && child.Attribute("ref") is { } reference) Add(child.Name, QName(child, reference));
                if ((child.Name == Xsd + "extension" || child.Name == Xsd + "restriction") && child.Attribute("base") is { } baseType)
                {
                    Add(Xsd + "simpleType", QName(child, baseType));
                    Add(Xsd + "complexType", QName(child, baseType));
                }
                if (child.Name == Xsd + "union" && child.Attribute("memberTypes") is { } memberTypes)
                {
                    if (memberTypes.Value.Length > 4096) throw new InvalidDataException("The XSD union member list exceeds the length limit.");
                    foreach (var memberType in memberTypes.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                        Add(Xsd + "simpleType", QName(child, memberType));
                }
                if (cost > MaxSchemaNodes * 4) throw new InvalidDataException("The XSD exceeds the schema expansion limit.");
            }
            active.Remove(node);
            return costs[node] = cost;

            void Add(XName kind, XName name)
            {
                foreach (var target in globals[(kind, name.LocalName)])
                {
                    // A schema without a target namespace can adopt the namespace of its includer.
                    var ns = TargetNamespace(target.Parent!);
                    if (ns.Length == 0 || ns == name.NamespaceName) cost += Cost(target, depth + 1);
                    if (cost > MaxSchemaNodes * 4) throw new InvalidDataException("The XSD exceeds the schema expansion limit.");
                }
            }
        }

        static bool IsGroup(XElement element) => element.Name == Xsd + "group" || element.Name == Xsd + "attributeGroup";
        static bool IsType(XElement element) => element.Name == Xsd + "simpleType" || element.Name == Xsd + "complexType";
    }

    private static bool BoundedPattern(string? pattern)
    {
        if (pattern is not { Length: <= 128 }) return false;
        var inClass = false;
        var variable = 0;
        var fixedRepeat = 0;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (++i == pattern.Length || !"dDsSwWnrt\\|.-?*+{}()[]^$".Contains(pattern[i])) return false;
            }
            else if (ch == '[')
            {
                if (inClass) return false;
                inClass = true;
            }
            else if (ch == ']') inClass = false;
            else if (!inClass)
            {
                if (ch is '(' or ')' or '|') return false;
                if (ch is '?' or '+' or '*') variable++;
                if (ch == '{')
                {
                    var end = pattern.IndexOf('}', i + 1);
                    if (end < 0) return false;
                    var bounds = pattern[(i + 1)..end].Split(',');
                    if (bounds.Length > 2 || !ParseBound(bounds[0], out var min)) return false;
                    var max = min;
                    if (bounds.Length == 2 && bounds[1].Length != 0 && !ParseBound(bounds[1], out max)) return false;
                    if (bounds.Length == 2 && (bounds[1].Length == 0 || min != max)) variable++;
                    fixedRepeat += Math.Max(min, max);
                    i = end;
                }
                if (variable > 1 || fixedRepeat > 128) return false;
            }
        }
        return !inClass;

        static bool ParseBound(string value, out int number) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number <= 128;
    }

    private sealed class Inspector(XmlSchemaSet schemas)
    {
        private readonly HashSet<string> warnings = new(StringComparer.Ordinal) { Scope };
        private readonly HashSet<XmlSchemaType> activeTypes = [];
        private readonly HashSet<XmlQualifiedName> substitutionHeads = schemas.GlobalElements.Values.OfType<XmlSchemaElement>()
            .Select(element => element.SubstitutionGroup).Where(name => !name.IsEmpty).ToHashSet();
        private int remaining = MaxShapeItems;

        public SoapInspectionResult Read(IReadOnlyList<ContractDocument> documents)
        {
            var messages = new Dictionary<XName, XElement>();
            var portTypes = new Dictionary<XName, XElement>();
            foreach (var document in documents)
            {
                var ns = XNamespace.Get(TargetNamespace(document.Element));
                Index(wsdlName: "message", messages);
                Index(wsdlName: "portType", portTypes);

                void Index(string wsdlName, Dictionary<XName, XElement> index)
                {
                    foreach (var element in document.Element.Elements(Wsdl + wsdlName))
                        if (!index.TryAdd(ns + NcName(element, "name"), element))
                            throw new InvalidDataException("The WSDL contains duplicate port type or message declarations.");
                }
            }

            var operations = new List<SoapOperation>();
            foreach (var (portType, declaration) in portTypes)
            {
                var operationNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var operation in declaration.Elements(Wsdl + "operation"))
                {
                    Spend();
                    var name = NcName(operation, "name");
                    if (!operationNames.Add(name)) throw new InvalidDataException("Overloaded WSDL port type operations are not supported.");
                    var input = Single(operation, "input");
                    var output = Single(operation, "output");
                    if (input is null && output is null) throw new InvalidDataException("A WSDL operation has no input or output.");
                    if (output is not null && operation.Elements().FirstOrDefault(element => element == input || element == output) == output)
                        warnings.Add("Output-first WSDL operations are represented by input and output direction, not exchange order.");
                    operations.Add(new(Name(portType), name, ReadMessage(input), ReadMessage(output)));
                }
            }
            if (operations.Count == 0) warnings.Add("The WSDL contains no port type operations.");
            return new(operations.OrderBy(operation => operation.PortType, StringComparer.Ordinal).ThenBy(operation => operation.Name, StringComparer.Ordinal).ToArray(),
                warnings.Order(StringComparer.Ordinal).ToArray());

            SoapMessage? ReadMessage(XElement? direction)
            {
                if (direction is null) return null;
                if (direction.Attribute("message") is not { } reference)
                    throw new InvalidDataException("A WSDL operation has no message reference.");
                var messageName = QName(direction, reference);
                if (!messages.TryGetValue(messageName, out var message))
                    throw new InvalidDataException("A WSDL operation references an unknown message.");
                var fields = new List<SoapField>();
                var parts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in message.Elements(Wsdl + "part"))
                {
                    Spend();
                    var name = NcName(part, "name");
                    if (!parts.Add(name)) throw new InvalidDataException("A WSDL message contains duplicate part names.");
                    var element = part.Attribute("element");
                    var type = part.Attribute("type");
                    if (element is not null && type is null)
                    {
                        if (schemas.GlobalElements[SchemaName(QName(part, element))] is not XmlSchemaElement schemaElement)
                            throw new InvalidDataException("A WSDL message part references an unknown XSD element.");
                        fields.Add(Element(schemaElement, required: true, depth: 0) with { Part = name });
                    }
                    else if (type is not null && element is null)
                    {
                        var typeName = SchemaName(QName(part, type));
                        var schemaType = schemas.GlobalTypes[typeName] as XmlSchemaType;
                        if (schemaType is null && typeName.Namespace == Xsd.NamespaceName)
                            schemaType = XmlSchemaType.GetBuiltInSimpleType(typeName) ?? (XmlSchemaType?)XmlSchemaType.GetBuiltInComplexType(typeName);
                        if (schemaType is null) throw new InvalidDataException("A WSDL message part references an unknown XSD type.");
                        fields.Add(new(name, Name(typeName), "part", true, "1", "1", false, Fields(schemaType, 0), name, BaseType(schemaType)));
                    }
                    else throw new InvalidDataException("A WSDL message part must reference exactly one XSD element or type.");
                }
                return new(Name(messageName), fields);
            }
        }

        private static XElement? Single(XElement operation, string role)
        {
            var elements = operation.Elements(Wsdl + role).Take(2).ToArray();
            if (elements.Length > 1) throw new InvalidDataException("A WSDL operation has duplicate input or output declarations.");
            return elements.FirstOrDefault();
        }

        private void Spend()
        {
            if (--remaining < 0) throw new InvalidDataException("The WSDL exceeds the message field or operation limit.");
        }

        private SoapField Element(XmlSchemaElement element, bool required, int depth)
        {
            Spend();
            var declaration = !element.RefName.IsEmpty ? (XmlSchemaElement?)schemas.GlobalElements[element.RefName] ?? element : element;
            var type = element.ElementSchemaType;
            if (declaration.IsAbstract || !declaration.SubstitutionGroup.IsEmpty || substitutionHeads.Contains(declaration.QualifiedName))
                warnings.Add("Abstract elements and substitution groups are not fully represented.");
            if (declaration.DefaultValue is not null || declaration.FixedValue is not null || declaration.Constraints.Count != 0)
                warnings.Add("Schema defaults, fixed values and identity constraints are not compared.");
            return new(Name(element.QualifiedName), TypeName(type), "element", required && element.MinOccurs > 0,
                Occurs(element.MinOccurs), Occurs(element.MaxOccurs), declaration.IsNillable, Fields(type, depth), BaseType(type));
        }

        private IReadOnlyList<SoapField> Fields(XmlSchemaType? type, int depth)
        {
            if (type is not XmlSchemaComplexType complex) return [];
            if (depth >= MaxShapeDepth || !activeTypes.Add(type))
            {
                warnings.Add("Recursive or deeply nested XSD field shapes are shown only to the inspection depth limit.");
                return [];
            }
            try
            {
                if (complex.IsAbstract || complex.IsMixed || complex.AttributeWildcard is not null)
                    warnings.Add("Abstract types, mixed text content and attribute wildcards are not fully represented.");
                var fields = new List<SoapField>();
                Particle(complex.ContentTypeParticle, fields, required: true, depth + 1);
                foreach (var attribute in complex.AttributeUses.Values.OfType<XmlSchemaAttribute>()
                             .OrderBy(attribute => attribute.QualifiedName.Namespace, StringComparer.Ordinal)
                             .ThenBy(attribute => attribute.QualifiedName.Name, StringComparer.Ordinal))
                {
                    if (attribute.Use == XmlSchemaUse.Prohibited) continue;
                    Spend();
                    var required = attribute.Use == XmlSchemaUse.Required;
                    if (attribute.DefaultValue is not null || attribute.FixedValue is not null)
                        warnings.Add("Schema defaults, fixed values and identity constraints are not compared.");
                    fields.Add(new(Name(attribute.QualifiedName), TypeName(attribute.AttributeSchemaType), "attribute", required,
                        required ? "1" : "0", "1", false, [], BaseType: BaseType(attribute.AttributeSchemaType)));
                }
                return fields;
            }
            finally { activeTypes.Remove(type); }
        }

        private void Particle(XmlSchemaParticle particle, List<SoapField> fields, bool required, int depth)
        {
            Spend();
            switch (particle)
            {
                case XmlSchemaElement element:
                    fields.Add(Element(element, required, depth));
                    break;
                case XmlSchemaGroupBase group:
                    if (group.MinOccurs != 1 || group.MaxOccurs != 1 || group is XmlSchemaChoice)
                        warnings.Add("Choice and optional or repeating group boundaries are flattened; field required flags account for optional branches only.");
                    var ordered = group.Items.OfType<XmlSchemaParticle>();
                    if (group is XmlSchemaAll) ordered = ordered.OrderBy(item => Name(((XmlSchemaElement)item).QualifiedName), StringComparer.Ordinal);
                    foreach (var child in ordered) Particle(child, fields, required && group.MinOccurs > 0 && (group is not XmlSchemaChoice || group.Items.Count == 1), depth);
                    break;
                case XmlSchemaAny:
                    warnings.Add("Element wildcards are not represented as fields.");
                    break;
                case XmlSchemaGroupRef { Particle: not null } groupRef:
                    if (groupRef.MinOccurs != 1 || groupRef.MaxOccurs != 1)
                        warnings.Add("Choice and optional or repeating group boundaries are flattened; field required flags account for optional branches only.");
                    Particle(groupRef.Particle, fields, required && groupRef.MinOccurs > 0, depth);
                    break;
            }
        }

        private static string TypeName(XmlSchemaType? type) => type is null ? "unknown" : !type.QualifiedName.IsEmpty
            ? Name(type.QualifiedName) : type is XmlSchemaSimpleType ? "anonymous-simple" : "anonymous-complex";

        private string? BaseType(XmlSchemaType? type)
        {
            if (type is XmlSchemaSimpleType simple && (simple.QualifiedName.Namespace != Xsd.NamespaceName || simple.QualifiedName.IsEmpty))
            {
                warnings.Add("Simple type facets, list item types and union members are not compared.");
                if (simple.BaseXmlSchemaType is { } baseType && !baseType.QualifiedName.IsEmpty) return Name(baseType.QualifiedName);
            }
            if (type is XmlSchemaComplexType { ContentType: XmlSchemaContentType.TextOnly } complex && complex.Datatype is { } data)
            {
                warnings.Add("Simple type facets, list item types and union members are not compared.");
                return Name(XmlSchemaType.GetBuiltInSimpleType(data.TypeCode).QualifiedName);
            }
            return null;
        }

        private static string Occurs(decimal count) => count == decimal.MaxValue ? "unbounded" : count.ToString(CultureInfo.InvariantCulture);
    }
}
