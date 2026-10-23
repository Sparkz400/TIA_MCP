using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Model;
using static TiaMcp.Core.HmiComponents.HmiComponentReview;

namespace TiaMcp.Core.HmiComponents;

internal static class HmiComponentXml
{
    internal static XDocument Read(JsonObject file, Func<JsonObject, byte[]> readFile, ref long examinedBytes)
    {
        examinedBytes += (int)file["bytes"]!;
        if (examinedBytes > 64L * 1024 * 1024) throw Error("Component XML review exceeds 64 MiB including repeated roles.");
        byte[] bytes = readFile(file);
        if (bytes.Length != (int)file["bytes"]! || Convert.ToHexStringLower(SHA256.HashData(bytes)) != Text(file, "sha256"))
            throw new BridgeException("ArtifactChanged", "Component XML bytes differ from the retained descriptor.");
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = FileLimit });
        int events = 0;
        while (reader.Read()) if (reader.Depth > 64 || ++events > 200000) throw Error("Component XML exceeds depth/event bounds.");
        var document = HmiPreservationRules.ReadXml(text);
        if (document.Declaration?.Encoding is string encoding && encoding.Length != 0 && !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only UTF-8 XML is supported.");
        return document;
    }

    internal static JsonObject Descriptor(JsonObject file) => new() { ["id"] = file["id"]!.DeepClone(), ["fileName"] = file["fileName"]!.DeepClone(),
        ["sha256"] = file["sha256"]!.DeepClone(), ["bytes"] = file["bytes"]!.DeepClone() };
}
