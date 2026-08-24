using System.Reflection;
using System.Xml.Linq;

namespace EtherCAT.NET.Engine.Esi;

/// <summary>
/// Parses ESI (EtherCAT Slave Information) XML files into the <see cref="EsiDeviceLibrary"/>
/// domain model.
/// </summary>
/// <remarks>
/// Deliberately built on <see cref="XDocument"/>/<see cref="XElement"/> rather than
/// <see cref="System.Xml.Serialization.XmlSerializer"/>: ESI numeric fields mix EtherCAT's
/// <c>#xHEX</c> notation with plain decimal literals (see <see cref="EsiNumber"/>), a shape
/// <c>XmlSerializer</c> has no attribute for. Reading straight from a <see cref="Stream"/> (never
/// a pre-decoded <see cref="string"/>/<see cref="TextReader"/>) lets the underlying
/// <see cref="System.Xml.XmlReader"/> honor the file's own encoding declaration (real ESI files
/// commonly declare <c>iso-8859-1</c>).
/// </remarks>
public static class EsiXmlParser
{
    /// <summary>File name of the Panasonic MINAS A6BE ESI file embedded in this assembly.</summary>
    public const string PanasonicMinasA6BeResourceFileName = "panasonic_minas-a6be_v1_9.xml";

    /// <summary>
    /// Loads and parses the Panasonic MINAS A6BE ESI file embedded as a resource in this assembly.
    /// </summary>
    public static EsiDeviceLibrary ParseEmbeddedPanasonicMinasA6Be() =>
        ParseEmbeddedResource(PanasonicMinasA6BeResourceFileName, typeof(EsiXmlParser).Assembly);

    /// <summary>
    /// Loads and parses an ESI XML file embedded as a resource in <paramref name="assembly"/>.
    /// The resource is located by matching the end of its manifest resource name against
    /// <paramref name="resourceFileName"/>, so callers do not need to know the exact
    /// namespace-qualified resource name the compiler generated.
    /// </summary>
    /// <param name="resourceFileName">File name of the embedded resource, e.g. <c>panasonic_minas-a6be_v1_9.xml</c>.</param>
    /// <param name="assembly">Assembly the resource is embedded in. Defaults to the assembly containing this parser.</param>
    /// <exception cref="InvalidOperationException">No embedded resource ending in <paramref name="resourceFileName"/> was found.</exception>
    public static EsiDeviceLibrary ParseEmbeddedResource(string resourceFileName, Assembly? assembly = null)
    {
        assembly ??= typeof(EsiXmlParser).Assembly;

        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceFileName, StringComparison.Ordinal));

        if (resourceName is null)
        {
            var available = string.Join(", ", assembly.GetManifestResourceNames());
            throw new InvalidOperationException(
                $"No embedded resource ending in '{resourceFileName}' was found in assembly '{assembly.FullName}'. " +
                $"Available resources: {available}");
        }

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' could not be opened.");

        return Parse(stream);
    }

    /// <summary>
    /// Parses an ESI XML document from a stream. The stream is read as-is (not pre-decoded) so
    /// the XML reader can honor the document's own encoding declaration.
    /// </summary>
    public static EsiDeviceLibrary Parse(Stream xmlStream)
    {
        var document = XDocument.Load(xmlStream);
        return ParseDocument(document);
    }

    /// <summary>Parses an ESI XML document already loaded into memory as text.</summary>
    public static EsiDeviceLibrary Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        return ParseDocument(document);
    }

    private static EsiDeviceLibrary ParseDocument(XDocument document)
    {
        var root = document.Root
            ?? throw new FormatException("ESI document has no root element.");

        var vendorElement = root.Element("Vendor")
            ?? throw new FormatException("ESI document has no <Vendor> element.");

        var vendor = ParseVendor(vendorElement);

        // Use Descendants rather than assuming a fixed <Descriptions>/<Groups>/<Devices> depth:
        // real ESI files nest many repeated <Device> blocks (e.g. one per wattage variant) that
        // all share the same Sm/Fmmu/RxPdo/TxPdo/Mailbox/Dc structure but differ in
        // ProductCode/RevisionNo/Name. All of them are parsed into a single flat list.
        var devices = root.Descendants("Device")
            .Select(ParseDevice)
            .ToList();

        return new EsiDeviceLibrary(vendor, devices);
    }

    private static EsiVendor ParseVendor(XElement vendorElement)
    {
        var idElement = vendorElement.Element("Id")
            ?? throw new FormatException("<Vendor> has no <Id> element.");

        var id = (uint)EsiNumber.Parse(idElement.Value);

        var nameElement = vendorElement.Elements("Name").FirstOrDefault(e => (string?)e.Attribute("LcId") == "1033")
            ?? vendorElement.Elements("Name").FirstOrDefault();

        return new EsiVendor(id, nameElement?.Value.Trim() ?? string.Empty);
    }

    private static EsiDeviceDescriptor ParseDevice(XElement deviceElement)
    {
        var typeElement = deviceElement.Element("Type")
            ?? throw new FormatException("<Device> has no <Type> element.");

        var name = typeElement.Value.Trim();
        var productCode = (uint)EsiNumber.Parse(RequireAttribute(typeElement, "ProductCode"));
        var revisionNumber = (uint)EsiNumber.Parse(RequireAttribute(typeElement, "RevisionNo"));

        var syncManagers = ParseSyncManagers(deviceElement);
        var fmmus = ParseFmmus(deviceElement);
        var rxPdos = deviceElement.Elements("RxPdo").Select(ParsePdo).ToList();
        var txPdos = deviceElement.Elements("TxPdo").Select(ParsePdo).ToList();
        var mailbox = ParseMailbox(deviceElement);
        var dc = ParseDc(deviceElement);

        return new EsiDeviceDescriptor(
            name,
            productCode,
            revisionNumber,
            syncManagers,
            fmmus,
            rxPdos,
            txPdos,
            mailbox,
            dc);
    }

    private static IReadOnlyList<EsiSyncManager> ParseSyncManagers(XElement deviceElement) =>
        deviceElement.Elements("Sm")
            .Select((element, index) => new EsiSyncManager(
                index,
                element.Value.Trim(),
                (ushort)EsiNumber.Parse(RequireAttribute(element, "StartAddress")),
                (byte)EsiNumber.Parse(RequireAttribute(element, "ControlByte")),
                (ushort)EsiNumber.Parse(RequireAttribute(element, "DefaultSize")),
                ParseBool(element.Attribute("Enable")?.Value, true)))
            .ToList();

    private static IReadOnlyList<EsiFmmu> ParseFmmus(XElement deviceElement) =>
        deviceElement.Elements("Fmmu")
            .Select((element, index) => new EsiFmmu(index, element.Value.Trim()))
            .ToList();

    private static EsiPdo ParsePdo(XElement pdoElement)
    {
        var indexElement = pdoElement.Element("Index")
            ?? throw new FormatException("PDO mapping has no <Index> element.");

        var index = (ushort)EsiNumber.Parse(indexElement.Value);
        var name = pdoElement.Element("Name")?.Value.Trim() ?? string.Empty;
        var isFixed = ParseBool(pdoElement.Attribute("Fixed")?.Value, false);

        var smAttribute = pdoElement.Attribute("Sm");
        int? syncManager = smAttribute is not null ? (int)EsiNumber.Parse(smAttribute.Value) : null;

        var entries = pdoElement.Elements("Entry").Select(ParsePdoEntry).ToList();

        return new EsiPdo(index, name, syncManager, isFixed, entries);
    }

    private static EsiPdoEntry ParsePdoEntry(XElement entryElement)
    {
        var indexElement = entryElement.Element("Index")
            ?? throw new FormatException("PDO entry has no <Index> element.");
        var subIndexElement = entryElement.Element("SubIndex")
            ?? throw new FormatException("PDO entry has no <SubIndex> element.");
        var bitLenElement = entryElement.Element("BitLen")
            ?? throw new FormatException("PDO entry has no <BitLen> element.");

        var index = (ushort)EsiNumber.Parse(indexElement.Value);
        var subIndex = (byte)EsiNumber.Parse(subIndexElement.Value);
        var bitLength = (int)EsiNumber.Parse(bitLenElement.Value);
        var name = entryElement.Element("Name")?.Value.Trim() ?? string.Empty;
        var dataType = entryElement.Element("DataType")?.Value.Trim() ?? string.Empty;

        return new EsiPdoEntry(index, subIndex, bitLength, name, dataType);
    }

    private static EsiMailboxConfig? ParseMailbox(XElement deviceElement)
    {
        // Two distinct <Mailbox> elements can appear in a <Device>: <Info>/<Mailbox>/<Timeout>
        // (request/response timeouts) and the top-level <Mailbox> (DataLinkLayer + <CoE>).
        // XElement.Element only looks at immediate children, so these two never collide.
        var mailboxElement = deviceElement.Element("Mailbox");
        var timeoutElement = deviceElement.Element("Info")?.Element("Mailbox")?.Element("Timeout");

        if (mailboxElement is null && timeoutElement is null)
        {
            return null;
        }

        var dataLinkLayer = ParseBool(mailboxElement?.Attribute("DataLinkLayer")?.Value, false);
        var coeElement = mailboxElement?.Element("CoE");

        var requestTimeout = timeoutElement?.Element("RequestTimeout") is { } req ? (int)EsiNumber.Parse(req.Value) : 0;
        var responseTimeout = timeoutElement?.Element("ResponseTimeout") is { } resp ? (int)EsiNumber.Parse(resp.Value) : 0;

        return new EsiMailboxConfig(
            dataLinkLayer,
            requestTimeout,
            responseTimeout,
            ParseBool(coeElement?.Attribute("SdoInfo")?.Value, false),
            ParseBool(coeElement?.Attribute("PdoUpload")?.Value, false),
            ParseBool(coeElement?.Attribute("PdoAssign")?.Value, false),
            ParseBool(coeElement?.Attribute("PdoConfig")?.Value, false),
            ParseBool(coeElement?.Attribute("SegmentedSdo")?.Value, false),
            ParseBool(coeElement?.Attribute("CompleteAccess")?.Value, false),
            ParseBool(coeElement?.Attribute("DiagHistory")?.Value, false));
    }

    private static EsiDcConfig? ParseDc(XElement deviceElement)
    {
        var dcElement = deviceElement.Element("Dc");
        if (dcElement is null)
        {
            return null;
        }

        var opModes = dcElement.Elements("OpMode").Select(ParseOpMode).ToList();
        return new EsiDcConfig(opModes);
    }

    private static EsiOpMode ParseOpMode(XElement opModeElement)
    {
        var name = opModeElement.Element("Name")?.Value.Trim() ?? string.Empty;
        var description = opModeElement.Element("Desc")?.Value.Trim() ?? string.Empty;

        var assignActivate = opModeElement.Element("AssignActivate") is { } assign
            ? EsiNumber.Parse(assign.Value)
            : 0UL;

        var cycleTimeElement = opModeElement.Element("CycleTimeSync0");
        var cycleTimeFactor = cycleTimeElement?.Attribute("Factor") is { } factor ? (int)EsiNumber.Parse(factor.Value) : 0;
        var cycleTime = cycleTimeElement is not null ? (long)EsiNumber.Parse(cycleTimeElement.Value) : 0L;

        var shiftTime = opModeElement.Element("ShiftTimeSync0") is { } shift ? (long)EsiNumber.Parse(shift.Value) : 0L;

        return new EsiOpMode(name, description, assignActivate, cycleTimeFactor, cycleTime, shiftTime);
    }

    private static bool ParseBool(string? text, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return defaultValue;
        }

        return text.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => bool.TryParse(text, out var parsed) ? parsed : defaultValue,
        };
    }

    private static string RequireAttribute(XElement element, string attributeName) =>
        element.Attribute(attributeName)?.Value
            ?? throw new FormatException($"<{element.Name.LocalName}> has no '{attributeName}' attribute.");
}
