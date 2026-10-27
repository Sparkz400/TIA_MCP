using System.IO.Compression;
using System.Text;
using System.Xml;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class AlarmWorkbookTests
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    internal static Task RowsAndBounds()
    {
        string path = Workbook(
            "<si><t>Name</t></si><si><r><t>Mo</t></r><r><t>tor</t></r></si>",
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"inlineStr\"><is><t>de-DE</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>1</v></c><c r=\"B2\"><v>7</v></c><c t=\"inlineStr\"><is><t>" + new string('x', 5000) + "</t></is></c></row>" +
            "<row r=\"3\"><c r=\"A3\"><v>3</v></c></row>");
        try
        {
            int taken = 0;
            var sheet = AlarmWorkbook.Read(path, () => ++taken <= 2)[0];
            Check.Equal("Lists", (string?)sheet["name"]);
            Check.Equal(true, (bool)sheet["rowsTruncated"]!);
            Check.Equal(true, (bool)sheet["cellTextTruncated"]!);
            var rows = sheet["rows"]!;
            Check.Equal(2, rows.Count());
            Check.Equal("Name", (string?)rows[0]!["cells"]!["A"]);
            Check.Equal("de-DE", (string?)rows[0]!["cells"]!["B"]);
            Check.Equal("Motor", (string?)rows[1]!["cells"]!["A"]);
            Check.Equal("7", (string?)rows[1]!["cells"]!["B"]);
            Check.Equal(AlarmWorkbook.MaxCellCharacters, ((string?)rows[1]!["cells"]!["#3"])!.Length);
            Check.Equal(2, (int)rows[1]!["row"]!);
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }

    internal static async Task RejectsDtd()
    {
        string path = Workbook("", "<row r=\"1\"/>", "<!DOCTYPE x [<!ENTITY e \"boom\">]>");
        try { await Check.ThrowsAsync<XmlException>(() => { AlarmWorkbook.Read(path, () => true); return Task.CompletedTask; }); }
        finally { File.Delete(path); }
    }

    private static string Workbook(string sharedStrings, string rows, string sheetPrefix = "")
    {
        string path = Path.Combine(Path.GetTempPath(), "alarm-workbook-" + Guid.NewGuid().ToString("N") + ".xlsx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
        Part("xl/workbook.xml", "<workbook xmlns=\"" + Main + "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Lists\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"/xl/worksheets/sheet1.xml\"/></Relationships>");
        Part("xl/sharedStrings.xml", "<sst xmlns=\"" + Main + "\">" + sharedStrings + "</sst>");
        Part("xl/worksheets/sheet1.xml", sheetPrefix + "<worksheet xmlns=\"" + Main + "\"><sheetData>" + rows + "</sheetData></worksheet>");
        return path;
    }
}
