using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace TiaMcp.Graph;

/// <summary>Offline GRAPH v6 structure reader. It neither evaluates conditions nor writes a native edit.</summary>
public sealed class GraphDocument
{
    public const string V6Namespace = "http://www.siemens.com/automation/Openness/SW/NetworkSource/Graph/v6";
    public const string Profile = "graph-v6-structure.v2";
    public const int MaxInputBytes = 8 * 1024 * 1024;
    public const int MaxReportBytes = 96 * 1024 * 1024;
    public const int MaxElements = 100_000;
    public const int MaxDepth = 128;
    public const int MaxPathCharacters = 16_384;
    public const int MaxTotalPathCharacters = 16 * 1024 * 1024;
    private readonly byte[] original;
    internal XDocument Xml { get; }
    internal IReadOnlyDictionary<XElement, string> Paths { get; }
    public GraphInspection Inspection { get; }
    internal static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private GraphDocument(byte[] bytes, XDocument xml)
    {
        original = bytes;
        Xml = xml;
        Paths = BuildPaths(xml.Root!);
        Inspection = new GraphReader(this).Read(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
    }

    public static GraphDocument Parse(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxInputBytes)
            throw new GraphException("InputSize", $"XML must contain 1 through {MaxInputBytes} bytes.");
        var retained = bytes.ToArray();
        try
        {
            // Preflight depth and element count before building the tree. DTDs and all external resolution are disabled.
            using (var reader = CreateReader(retained))
            {
                int count = 0;
                while (reader.Read())
                    if (reader.Depth > MaxDepth || (reader.NodeType == XmlNodeType.Element && ++count > MaxElements))
                        throw new GraphException("XmlCapacity", "XML exceeds the depth or element limit.");
            }
            using var treeReader = CreateReader(retained);
            return new GraphDocument(retained, XDocument.Load(treeReader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo));
        }
        catch (XmlException ex) { throw new GraphException("InvalidXml", ex.Message, ex); }
    }

    public static GraphDocument Load(string path)
    {
        using var source = File.OpenRead(path);
        return Read(source);
    }

    public static GraphDocument Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = source.Read(chunk, 0, Math.Min(chunk.Length, MaxInputBytes + 1 - (int)buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaxInputBytes) throw new GraphException("InputSize", "XML exceeds the input byte limit.");
        }
        return Parse(buffer.ToArray());
    }

    public byte[] GetOriginalBytes() => original.ToArray();
    public void WriteOriginal(Stream destination) => destination.Write(original);
    public string ToJson() => Serialize(Inspection);
    public string ToHtml() => GraphHtml.Render(this);
    public GraphComparison CompareTo(GraphDocument after) => GraphComparer.Compare(this, after);

    internal static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxReportBytes) throw new GraphException("ReportSize", "Report exceeds the output limit.");
        return json;
    }

    private static XmlReader CreateReader(byte[] bytes) => XmlReader.Create(new MemoryStream(bytes, writable: false), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxInputBytes,
        IgnoreComments = false, IgnoreWhitespace = false, CloseInput = true
    });

    private static Dictionary<XElement, string> BuildPaths(XElement root)
    {
        var paths = new Dictionary<XElement, string>();
        long totalCharacters = 0;
        void Visit(XElement element, string path)
        {
            totalCharacters += path.Length;
            if (path.Length > MaxPathCharacters || totalCharacters > MaxTotalPathCharacters)
                throw new GraphException("XmlCapacity", "XML exceeds the diagnostic path character budget.");
            paths[element] = path;
            var counts = new Dictionary<XName, int>();
            foreach (var child in element.Elements())
            {
                counts.TryGetValue(child.Name, out var n);
                counts[child.Name] = ++n;
                Visit(child, $"{path}/{child.Name}[{n}]");
            }
        }
        Visit(root, $"/{root.Name}[1]");
        return paths;
    }
}
