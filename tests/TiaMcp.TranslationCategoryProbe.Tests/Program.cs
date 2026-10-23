using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

// A file-only check: this assembly does not reference or load Siemens assemblies.
if (args.Length != 2) throw new ArgumentException("Expected retained genuine PLC workbook and fresh output directory.");
string genuine = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new InvalidOperationException("Fresh test output required.");
Directory.CreateDirectory(output);
var results = new List<object>();
XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
XNamespace r = "http://schemas.openxmlformats.org/package/2006/relationships";
var baseline = TranslationCategoryWorkbook.Read(genuine);
if (baseline.Count != 4) throw new Exception("Expected the genuine retained four-row PLC capture.");
results.Add(new { name = "genuine captured workbook decodes all four rows", passed = true, synthetic = false });
string projectName = "TiaMcpScratch_20261015_071342_e395a300", plcName = "ScratchPLC";
var selected = baseline.Where(p => p.Value.ViewPath.Contains("\\Main\\", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value);
Check("explicit synthetic Main subset exercises populated gate", TranslationCategoryWorkbook.PopulatedMainControl(baseline, selected, projectName, plcName));
Check("empty Main cannot authorize restricted calls", !TranslationCategoryWorkbook.PopulatedMainControl(baseline, new(), projectName, plcName));
Check("PLC leakage cannot satisfy exact Main control", !TranslationCategoryWorkbook.PopulatedMainControl(baseline, baseline, projectName, plcName));
Check("different project cannot satisfy exact Main control", !TranslationCategoryWorkbook.PopulatedMainControl(baseline, selected, "other", plcName));
Check("different PLC cannot satisfy exact Main control", !TranslationCategoryWorkbook.PopulatedMainControl(baseline, selected, projectName, "other"));
Check("missing required Main sentinel cannot authorize calls", !TranslationCategoryWorkbook.PopulatedMainControl(baseline, selected.Take(1).ToDictionary(p => p.Key, p => p.Value), projectName, plcName));
Check("all-row baseline equality", TranslationCategoryWorkbook.Equal(baseline, TranslationCategoryWorkbook.Read(genuine)));
Check("missing peer row fails baseline equality", !TranslationCategoryWorkbook.Equal(baseline, selected));
Reject("hidden-system-sheet", "xl/workbook.xml", xml => xml.Descendants(s + "sheet").Last().SetAttributeValue("state", "hidden"));
Reject("missing-system-sheet", "xl/workbook.xml", xml => xml.Descendants(s + "sheet").Last().Remove());
Reject("renamed-user-sheet", "xl/workbook.xml", xml => xml.Descendants(s + "sheet").First().SetAttributeValue("name", "Other"));
Reject("reordered-sheets", "xl/workbook.xml", xml => { var a = xml.Descendants(s + "sheet").First(); a.Remove(); xml.Element(s + "sheets").Add(a); });
Reject("external-sheet-relationship", "xl/_rels/workbook.xml.rels", xml => xml.Elements(r + "Relationship").First(x => ((string)x.Attribute("Target")).Contains("sheet1.xml")).SetAttributeValue("TargetMode", "External"));
Reject("changed-empty-sheet-language", "xl/worksheets/sheet2.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "F1").Element(s + "v").Value = "0");
Reject("formula-in-source-cell", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "E2").Add(new XElement(s + "f", "1+1")));
Reject("duplicated-row", "xl/worksheets/sheet1.xml", xml => xml.Element(s + "sheetData").Add(new XElement(xml.Descendants(s + "row").Last())));
Reject("duplicated-cell", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "row").Skip(1).First().Add(new XElement(xml.Descendants(s + "row").Skip(1).First().Elements().First())));
Reject("missing-target-cell", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "F2").Remove());
Reject("duplicate-native-key", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "C3").Element(s + "v").Value = xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "C2").Element(s + "v").Value);
Reject("negative-shared-string-index", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").First().Element(s + "v").Value = "-1");
Reject("rich-shared-string", "xl/sharedStrings.xml", xml => xml.Elements(s + "si").First().Add(new XElement(s + "r", new XElement(s + "t", "hidden text"))));
string changed = Mutate("changed-peer-target", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "F2").Element(s + "v").Value = "0");
Check("changed peer target fails baseline equality", !TranslationCategoryWorkbook.Equal(baseline, TranslationCategoryWorkbook.Read(changed)));
string changedMain = Mutate("changed-main-source", "xl/worksheets/sheet1.xml", xml => xml.Descendants(s + "c").Single(x => (string)x.Attribute("r") == "E3").Element(s + "v").Value = "0");
var changedRecords = TranslationCategoryWorkbook.Read(changedMain);
Check("changed Main source cannot satisfy populated gate", !TranslationCategoryWorkbook.PopulatedMainControl(changedRecords, selected, projectName, plcName));
File.WriteAllText(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(new { kind = "file-only-populated-category-gate-checks", allChecksPassed = true,
    checks = results, checkCount = results.Count, nativeExecuted = false, nativeImportAllowed = false, inputWorkbook = genuine,
    inputSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(genuine))), syntheticMutationsAreNotNativeEvidence = true }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count} file-only populated category gate checks passed; no Siemens assembly referenced or native call made.");

void Check(string name, bool condition)
{
    if (!condition) throw new Exception(name);
    results.Add(new { name, passed = true, synthetic = true });
}
string Mutate(string name, string part, Action<XElement> modify)
{
    string path = Path.Combine(output, name + ".synthetic.xlsx");
    using var input = ZipFile.OpenRead(genuine);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in input.Entries)
    {
        using var destination = archive.CreateEntry(entry.FullName).Open();
        using var original = entry.Open();
        if (entry.FullName == part) { var xml = XElement.Load(original); modify(xml); xml.Save(destination); }
        else original.CopyTo(destination);
    }
    return path;
}
void Reject(string name, string part, Action<XElement> change)
{
    string path = Mutate(name, part, change);
    try { TranslationCategoryWorkbook.Read(path); }
    catch (InvalidDataException) { Check(name + " refused", true); return; }
    throw new Exception(name + " was accepted");
}
