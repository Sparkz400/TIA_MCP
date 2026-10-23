using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace TiaMcp.Core.Inventory;

/// <summary>Streaming inventory exports; every spreadsheet cell is a literal string.</summary>
public static class InventoryExport
{
    private static readonly string[] Headers = ["Name", "Device", "Kind", "Plant designation", "Location identifier", "Product designation",
        "Configured MLFB", "Configured firmware", "Configured hardware revision", "Slot / position", "Configured IPs",
        "Actual MLFB", "Installed firmware", "Serial number", "Actual hardware revision", "Online status", "Online observed UTC",
        "MLFB comparison", "Firmware comparison", "Hardware comparison", "Object path", "Project path", "Missing data and reasons",
        "Field provenance", "Online endpoint", "Online hardware ID", "Snapshot ID", "Capture scope complete"];
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    public static object Write(InventoryStore.Snapshot snapshot, string directory, string format)
    {
        if (format is not ("csv" or "xlsx")) throw new BridgeException("InvalidRequest", "format must be csv or xlsx.");
        Directory.CreateDirectory(directory);
        string stem = Path.Combine(directory, "inventory-" + snapshot.Metadata.SnapshotId + "-" + Guid.NewGuid().ToString("N"));
        string path = stem + "." + format;
        string metadataPath = stem + ".metadata.json";
        bool artifactCreated = false, metadataCreated = false;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                artifactCreated = true;
                if (format == "csv") WriteCsv(stream, snapshot);
                else WriteXlsx(stream, snapshot);
            }
            using (var stream = new FileStream(metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                metadataCreated = true;
                JsonSerializer.Serialize(stream, new { snapshot = snapshot.Metadata, rowsExported = snapshot.Metadata.TotalRows,
                    formulaPolicy = "CSV formula-like text is prefixed with an apostrophe; XLSX uses inline strings, never formulas.",
                    comparisonPolicy = "Only available values compared. Whitespace normalized in MLFB; V prefix and omitted zero patch normalized in firmware. Catalog wildcard MLFB is not comparable. Differences do not determine compatibility.",
                    fieldPolicy = "Unavailable values retain [state] and detailed reasons. IPs can be aggregated from explicitly recorded descendant interfaces. BMK components are never parsed from names." }, InventoryStore.Json);
            }
            return new { snapshot = snapshot.Metadata, rowsExported = snapshot.Metadata.TotalRows, artifact = Artifact(path), metadataArtifact = Artifact(metadataPath) };
        }
        catch
        {
            // Only files successfully created by this call belong to its cleanup.
            try { if (metadataCreated) File.Delete(metadataPath); }
            finally { if (artifactCreated) File.Delete(path); }
            throw;
        }
    }

    private static object Artifact(string path)
    {
        using var stream = File.OpenRead(path);
        return new { path = Path.GetFullPath(path), bytes = stream.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)) };
    }

    private static IEnumerable<string[]> Values(InventoryStore.Snapshot snapshot)
    {
        var metadata = snapshot.Metadata;
        yield return Headers;
        foreach (var row in snapshot.ReadRows())
        {
            string C(string key) => Display(row.Configured[key]); string A(string key) => Display(row.Actual[key]);
            var missing = row.Configured.Select(f => (Key: "configured." + f.Key, Field: f.Value))
                .Concat(row.Actual.Select(f => (Key: "actual." + f.Key, Field: f.Value)))
                .Where(f => f.Field.State != "available").Select(f => f.Key + "=" + f.Field.State + ": " + f.Field.Reason);
            var provenance = row.Configured.ToDictionary(f => "configured." + f.Key, f => f.Value.Source)
                .Concat(row.Actual.Select(f => new KeyValuePair<string, string>("actual." + f.Key, f.Value.Source)));
            yield return [row.Name, row.DeviceName, row.Kind, C("plantDesignation"), C("locationIdentifier"), C("productDesignation"),
                C("mlfb"), C("firmware"), C("hardwareRevision"), C("slot"), C("ip"), A("mlfb"), A("firmware"), A("serialNumber"), A("hardwareRevision"),
                row.Online.Status, row.Online.ObservedAtUtc?.ToString("O") ?? "[notRead]", row.Comparisons["mlfb"], row.Comparisons["firmware"],
                row.Comparisons["hardwareRevision"], row.Path, row.ProjectPath, string.Join("; ", missing),
                JsonSerializer.Serialize(provenance.ToDictionary(), InventoryStore.Json), row.Online.Endpoint ?? "[notRead]",
                row.Online.HardwareId?.ToString(CultureInfo.InvariantCulture) ?? "[notRead]", metadata.SnapshotId,
                metadata.Coverage.ScopeComplete ? "true" : "false"];
        }
    }

    private static string Display(InventoryField field) => field.State == "available" ? field.Value! : "[" + field.State + "]";
    public static string CsvCell(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || value.Length > 0 && value[0] is '\t' or '\r' or '\n') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static void WriteCsv(Stream stream, InventoryStore.Snapshot snapshot)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
        foreach (var row in Values(snapshot)) writer.WriteLine(string.Join(",", row.Select(CsvCell)));
    }

    private static void WriteXlsx(Stream stream, InventoryStore.Snapshot snapshot)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Text(zip, "[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
            """);
        Text(zip, "_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Text(zip, "xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Inventory" sheetId="1" r:id="rId1"/><sheet name="Capture details" sheetId="2" r:id="rId2"/></sheets></workbook>
            """);
        Text(zip, "xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        Text(zip, "xl/styles.xml", """
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><color rgb="FFFFFFFF"/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FF17365D"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="2"><xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="top"/></xf><xf numFmtId="49" fontId="1" fillId="2" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center" wrapText="1"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>
            """);
        Sheet(zip, "xl/worksheets/sheet1.xml", Values(snapshot), Headers.Length, true);
        var metadata = snapshot.Metadata;
        Sheet(zip, "xl/worksheets/sheet2.xml", new[] {
            new[] { "Capture detail", "Value" }, new[] { "Snapshot", metadata.SnapshotId },
            new[] { "Project", metadata.ProjectPath }, new[] { "Offline observed UTC", metadata.CapturedAtUtc.ToString("O") },
            new[] { "Rows", metadata.TotalRows.ToString(CultureInfo.InvariantCulture) },
            new[] { "Traversal complete", metadata.Coverage.ScopeComplete.ToString() },
            new[] { "Truncated", metadata.Coverage.Truncated.ToString() }, new[] { "Scope", metadata.Coverage.Scope },
            new[] { "Warnings", string.Join("; ", metadata.Coverage.Warnings) },
            new[] { "Hardware qualification", metadata.HardwareQualification },
            new[] { "Freshness", "Fixed observation, not live; per-row actual timestamps may differ." },
            new[] { "BMK", "PlantDesignation and LocationIdentifier are source fields. ProductDesignation stays unavailable if not exposed; names are not parsed." },
            new[] { "IP", "Configured addresses on this object or recorded descendant interfaces; not inferred for peer I/O modules." },
            new[] { "Comparison", "Missing values are notComparable; mismatch is a difference, not a compatibility verdict." },
            new[] { "Missing data", "[state] markers retain row-specific reasons in Inventory. No configured-to-actual fallback." }
        }, 2, false);
    }

    private static void Sheet(ZipArchive zip, string name, IEnumerable<string[]> rows, int columns, bool filter)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.None });
        xml.WriteStartElement("worksheet", MainNs);
        xml.WriteStartElement("sheetViews"); xml.WriteStartElement("sheetView"); xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane"); xml.WriteAttributeString("ySplit", "1"); xml.WriteAttributeString("topLeftCell", "A2"); xml.WriteAttributeString("activePane", "bottomLeft"); xml.WriteAttributeString("state", "frozen"); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
        xml.WriteStartElement("cols");
        for (int c = 1; c <= columns; c++)
        {
            xml.WriteStartElement("col"); xml.WriteAttributeString("min", c.ToString()); xml.WriteAttributeString("max", c.ToString());
            xml.WriteAttributeString("width", !filter && c == 2 ? "120" : c >= 21 && c <= 24 ? "65" : "24"); xml.WriteAttributeString("customWidth", "1"); xml.WriteEndElement();
        }
        xml.WriteEndElement(); xml.WriteStartElement("sheetData"); int rowNumber = 0;
        foreach (var row in rows)
        {
            rowNumber++; xml.WriteStartElement("row"); xml.WriteAttributeString("r", rowNumber.ToString());
            if (rowNumber == 1) { xml.WriteAttributeString("ht", "32"); xml.WriteAttributeString("customHeight", "1"); }
            for (int c = 0; c < row.Length; c++)
            {
                if (row[c].Length > 32767) throw new BridgeException("ExportCellLimit", "An Excel cell exceeds 32767 characters; export CSV to preserve it.");
                xml.WriteStartElement("c"); xml.WriteAttributeString("r", Column(c + 1) + rowNumber); xml.WriteAttributeString("t", "inlineStr"); xml.WriteAttributeString("s", rowNumber == 1 ? "1" : "0");
                xml.WriteStartElement("is"); xml.WriteStartElement("t"); xml.WriteAttributeString("xml", "space", null, "preserve"); xml.WriteString(EncodeText(row[c])); xml.WriteEndElement(); xml.WriteEndElement(); xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        if (filter) { xml.WriteStartElement("autoFilter"); xml.WriteAttributeString("ref", "A1:" + Column(columns) + rowNumber); xml.WriteEndElement(); }
        xml.WriteEndElement();
    }
    // Match only the underscore so overlapping escape-looking sequences stay literal.
    private static string EncodeText(string value) => Regex.Replace(value, @"_(?=x[0-9A-Fa-f]{4}_)|[\x00-\x08\x0B-\x1F\uFFFE\uFFFF]",
        match => "_x" + ((int)match.Value[0]).ToString("X4", CultureInfo.InvariantCulture) + "_");
    private static string Column(int value) { string result = ""; while (value > 0) { value--; result = (char)('A' + value % 26) + result; value /= 26; } return result; }
    private static void Text(ZipArchive zip, string name, string content) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content); }
}
