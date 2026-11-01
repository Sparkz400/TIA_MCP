using System.IO.Compression;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class ProjectUpgradeRulesTests
{
    private static readonly string Hash = new('a', 64);

    private static async Task<string> Code(Action action) =>
        (await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(action))).Code;

    private static async Task Refused(string code, string path, string? hash = null) =>
        Check.Equal(code, await Code(() => ProjectUpgradeRules.SourceVersion(path, hash ?? Hash, out _)));

    internal static async Task VersionsAndRefusals()
    {
        foreach (var (name, version, archive) in new[] { ("p.ap14", "V14", false), ("p.ap15", "V15", false), ("p.AP15_1", "V15.1", false), ("p.ap16", "V16", false),
            ("p.ap20", "V20", false), ("a.zap14", "V14", true), ("a.ZAP15_1", "V15.1", true), ("a.zap20", "V20", true) })
        {
            Check.Equal(version, ProjectUpgradeRules.SourceVersion(@"D:\x\" + name, Hash.ToUpperInvariant(), out bool isArchive));
            Check.Equal(archive, isArchive);
        }
        // V13 and older and V21 and newer are refused here, before any file is read or native call made.
        foreach (string name in new[] { "p.ap13", "a.zap13", "p.ap12", "p.ap11", "p.ap13_1", "p.ap14_1", "p.ap21", "a.zap21", "p.ap22" })
            await Refused("ProjectVersionUnsupported", @"D:\x\" + name);
        foreach (string name in new[] { "l.al16", "a.zal16", "a.zip", "p.ap", "p.ap16.bak", "p" })
            await Refused("InvalidRequest", @"D:\x\" + name);
        foreach (string? hash in new[] { "", new string('a', 63), new string('g', 64), new string('a', 65) })
            await Refused("InvalidRequest", @"D:\x\p.ap13", hash);
        await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => ProjectUpgradeRules.SourceVersion(@"D:\x\p.ap16", null, out _)));
    }

    private static string Marker(string name, string compatibility) =>
        "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<Data Type=\"Project\" Name=\"" + name + "\" NameOfName=\"Siemens.Automation.ObjectFrame.ICoreAttributes.Name\" " +
        "BrowsableType=\"Siemens.Automation.DomainModel.ProjectData\" ProjectCompatibilityVersion=\"" + compatibility + "\" xmlns=\"x\">\r\n  <Icon>AAAA</Icon>";

    private static string Archive(string folder, string file, params (string Name, string Text)[] entries)
    {
        string path = Path.Combine(folder, file);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, text) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
        return path;
    }

    internal static async Task MarkerDeclaresTheRealVersion()
    {
        string folder = Directory.CreateTempSubdirectory("tia-upgrade-marker-").FullName;
        try
        {
            // The marker, not the file name, decides: a renamed V13 project or archive never reaches the native upgrade.
            string project = Path.Combine(folder, "Demo.ap16"), renamed = Path.Combine(folder, "Old.ap16"), point = Path.Combine(folder, "Point.ap15_1");
            File.WriteAllText(project, Marker("Demo &amp; Co", "16.0.0.0"));
            File.WriteAllText(renamed, Marker("Old", "13.0.1.0"));
            File.WriteAllText(point, Marker("Point", "15.1.0.0"));
            Check.Equal("Demo & Co", ProjectUpgradeRules.MarkerName(project, false, "V16"));
            Check.Equal("Point", ProjectUpgradeRules.MarkerName(point, false, "V15.1"));
            Check.Equal("ProjectVersionUnsupported", await Code(() => ProjectUpgradeRules.MarkerName(renamed, false, "V16")));
            Check.Equal("ProjectVersionUnsupported", await Code(() => ProjectUpgradeRules.MarkerName(point, false, "V15")));
            string plain = Path.Combine(folder, "Plain.ap16"), slash = Path.Combine(folder, "Slash.ap16");
            File.WriteAllText(plain, "<Data Type=\"Library\" Name=\"x\" ProjectCompatibilityVersion=\"16.0.0.0\">");
            File.WriteAllText(slash, Marker("a/b", "16.0.0.0"));
            Check.Equal("InvalidRequest", await Code(() => ProjectUpgradeRules.MarkerName(plain, false, "V16")));
            Check.Equal("InvalidRequest", await Code(() => ProjectUpgradeRules.MarkerName(slash, false, "V16")));
            // An archive's one top-level marker of its own release names the retrieved project (V14 SP1 archives declare 14.0).
            string good = Archive(folder, "Demo_1.zap14", ("Demo_1.ap14", Marker("Demo", "14.0.0.0")), ("System/PEData.plf", "x"), ("Logs/ConversionLog.xml", "x"));
            Check.Equal("Demo", ProjectUpgradeRules.MarkerName(good, true, "V14"));
            foreach (var (file, entries) in new (string, (string, string)[])[] {
                ("Two.zap14", new[] { ("A.ap14", Marker("A", "14.0.0.0")), ("B.ap14", Marker("B", "14.0.0.0")) }),
                ("Nested.zap14", new[] { ("Demo/Demo.ap14", Marker("Demo", "14.0.0.0")) }),
                ("None.zap14", new[] { ("System/PEData.plf", "x") }) })
                Check.Equal("InvalidRequest", await Code(() => ProjectUpgradeRules.MarkerName(Archive(folder, file, entries), true, "V14")));
            Check.Equal("ProjectVersionUnsupported", await Code(() => ProjectUpgradeRules.MarkerName(Archive(folder, "Old.zap14", ("Old.ap13", Marker("Old", "13.0.1.0"))), true, "V14")));
            Check.Equal("ProjectVersionUnsupported", await Code(() => ProjectUpgradeRules.MarkerName(Archive(folder, "Renamed.zap14", ("Renamed.ap14", Marker("Renamed", "13.0.1.0"))), true, "V14")));
            File.WriteAllText(Path.Combine(folder, "Broken.zap14"), "not a zip");
            await Check.ThrowsAsync<InvalidDataException>(() => Task.Run(() => ProjectUpgradeRules.MarkerName(Path.Combine(folder, "Broken.zap14"), true, "V14")));
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static async Task FolderHoldsOneProject()
    {
        string folder = Directory.CreateTempSubdirectory("tia-upgrade-single-").FullName;
        try
        {
            string project = Path.Combine(folder, "Demo.ap16");
            File.WriteAllText(project, "x");
            Directory.CreateDirectory(Path.Combine(folder, "System"));
            File.WriteAllText(Path.Combine(folder, "System", "PEData.plf"), "x");
            File.WriteAllText(Path.Combine(folder, "plcmArchive.pma14_1"), "x");
            ProjectUpgradeRules.RequireSingleProject(folder, project);
            foreach (string other in new[] { Path.Combine(folder, "Other.ap15"), Path.Combine(folder, "Backup.zap16"), Path.Combine(folder, "System", "Nested.ap16") })
            {
                File.WriteAllText(other, "x");
                Check.Equal("InvalidRequest", await Code(() => ProjectUpgradeRules.RequireSingleProject(folder, project)));
                File.Delete(other);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static async Task LayoutStaysOwnedAndBounded()
    {
        string root = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "tia-upgrade-" + Guid.NewGuid().ToString("N"), "u_12345678");
        // The folder keeps its name: V21 saves <folder>_V21/<folder>_V21.ap21 next to it (observed 31 October 2026).
        var project = ProjectUpgradeRules.Plan(root, @"E:\Examples\Demo\Main.ap16", false, "Main");
        Check.Equal(Path.Combine(root, "Demo", "Main.ap16"), project.Input);
        Check.Equal(root, project.Target);
        Check.Equal(Path.Combine(root, "Demo_V21", "Demo_V21.ap21"), project.Upgraded);
        Check.Equal(Path.Combine(root, "upgrade-receipt.json"), project.Receipt);
        // An archive's upgrade is named after the project its marker declares, not after the archive file.
        var archive = ProjectUpgradeRules.Plan(root, @"E:\Examples\Demo_1.zap14", true, "Demo");
        Check.Equal(Path.Combine(root, "s", "Demo_1.zap14"), archive.Input);
        Check.Equal(Path.Combine(root, "w"), archive.Target);
        Check.Equal(Path.Combine(root, "w", "Demo_V21", "Demo_V21.ap21"), archive.Upgraded);
        // Only the observed upgrade location is accepted.
        Check.Equal(true, ProjectUpgradeRules.InLayout(project, Path.Combine(root, "Demo_V21", "DEMO_V21.ap21")));
        foreach (string other in new[] { Path.Combine(root, "Demo", "Main.ap16"), Path.Combine(root, "Demo", "Demo_V21", "Demo_V21.ap21"), Path.Combine(root, "Main_V21", "Main_V21.ap21"), "", null! })
            Check.Equal(false, ProjectUpgradeRules.InLayout(project, other));
        Check.Equal(true, ProjectUpgradeRules.InLayout(archive, Path.Combine(root, "w", "Demo_V21", "Demo_V21.ap21")));
        foreach (string other in new[] { Path.Combine(root, "w", "Demo_1_V21", "Demo_1_V21.ap21"), Path.Combine(root, "w", "Demo_V21", "Other_V21.ap21"), Path.Combine(root, "Demo_V21", "Demo_V21.ap21"),
            Path.Combine(root, "w", "Demo", "Demo_V21", "Demo_V21.ap21"), Path.Combine(root, "w", "Demo_V21", "Demo_V21.ap16") })
            Check.Equal(false, ProjectUpgradeRules.InLayout(archive, other));
        // A project folder is copied whole, so the destination may not lie inside it; an archive is copied alone.
        string folder = Path.GetDirectoryName(root)!;
        foreach (string inside in new[] { Path.Combine(folder, "Demo.ap16"), Path.Combine(root, "Demo.ap16"), @"E:\Demo.ap16" })
            Check.Equal("InvalidRequest", await Code(() => ProjectUpgradeRules.Plan(root, inside, false, "Demo")));
        Check.Equal(Path.Combine(root, "s", "Demo.zap16"), ProjectUpgradeRules.Plan(root, Path.Combine(folder, "Demo.zap16"), true, "Demo").Input);
        Check.Equal(Path.Combine(root, Path.GetFileName(folder) + "x", "Demo.ap16"), ProjectUpgradeRules.Plan(root, Path.Combine(folder + "x", "Demo.ap16"), false, "Demo").Input);
        // The upgraded directory must fit TIA's 143-character limit: <folder>_V21, or w/<declared project>_V21 for an archive.
        string name = new('N', 143 - root.Length - 1 - "_V21".Length);
        ProjectUpgradeRules.Plan(root, @"E:\Examples\" + name + @"\Main.ap16", false, "Main");
        Check.Equal("ProjectPathTooLong", await Code(() => ProjectUpgradeRules.Plan(root, @"E:\Examples\" + name + @"N\Main.ap16", false, "Main")));
        string declared = new('N', 143 - Path.Combine(root, "w").Length - 1 - "_V21".Length);
        ProjectUpgradeRules.Plan(root, @"E:\Examples\Short.zap16", true, declared);
        Check.Equal("ProjectPathTooLong", await Code(() => ProjectUpgradeRules.Plan(root, @"E:\Examples\Short.zap16", true, declared + "N")));
        Check.Equal(false, Directory.Exists(folder));
    }

    internal static Task ReceiptProvesOnlyACompletedUpgradeAtTheLayout()
    {
        string examples = Directory.CreateTempSubdirectory("tia-upgrade-receipt-").FullName;
        try
        {
            string root = Path.Combine(examples, "u_12345678"), zap = Archive(examples, "Demo_1.zap14", ("Demo_1.ap14", Marker("Main", "14.0.0.0")));
            string demo = Path.Combine(examples, "Demo"), other = new('b', 64);
            JObject Receipt(string state, string source, string name, string project, string? sha = null) => new() { ["state"] = state,
                ["source"] = new JObject { ["path"] = source, ["projectName"] = name, ["sourceSha256"] = sha ?? Hash }, ["project"] = new JObject { ["path"] = project } };
            string folder = Path.Combine(root, "Demo_V21", "Demo_V21.ap21"), archive = Path.Combine(root, "w", "Main_V21", "Main_V21.ap21");
            string main = Path.Combine(demo, "Main.ap16");
            // A project folder is admitted as its folder, an archive as its file; the receipt must belong to that source and its current hash.
            Check.Equal(folder, ProjectUpgradeRules.ReceiptProject(Receipt("upgraded", main, "Main", folder), root, demo, false, Hash));
            Check.Equal(folder, ProjectUpgradeRules.ReceiptProject(Receipt("upgraded", main, "Main", folder), root, demo + @"\", false, Hash.ToUpperInvariant()));
            // An archive's layout name is what the admitted archive's marker declares (Main), whatever the receipt says.
            Check.Equal(archive, ProjectUpgradeRules.ReceiptProject(Receipt("upgraded", zap, "Main", archive), root, zap, true, Hash));
            Check.Equal(archive, ProjectUpgradeRules.ReceiptProject(Receipt("upgraded", zap, "Forged", archive), root, zap, true, Hash));
            // An interrupted copy or upgrade, another source or hash, a project outside the observed layout or a missing receipt prove nothing.
            foreach (var (receipt, source, isArchive, sha) in new (JObject?, string, bool, string?)[] {
                (Receipt("copying", main, "Main", folder), demo, false, Hash),
                (Receipt("upgrading", main, "Main", folder), demo, false, Hash),
                (Receipt("upgraded", main, "Main", folder), demo, false, other),
                (Receipt("upgraded", main, "Main", folder, other), demo, false, Hash),
                (Receipt("upgraded", main, "Main", folder), demo, false, null),
                (Receipt("upgraded", Path.Combine(examples, "Other", "Main.ap16"), "Main", folder), demo, false, Hash),
                (Receipt("upgraded", main, "Main", Path.Combine(root, "Demo", "Demo_V21.ap21")), demo, false, Hash),
                (Receipt("upgraded", main, "Main", Path.Combine(root, "Main_V21", "Main_V21.ap21")), demo, false, Hash),
                (Receipt("upgraded", zap, "Main", archive), Path.Combine(examples, "Demo_2.zap14"), true, Hash),
                (Receipt("upgraded", zap, "Demo_1", Path.Combine(root, "w", "Demo_1_V21", "Demo_1_V21.ap21")), zap, true, Hash),
                (Receipt("upgraded", zap, "Main", Path.Combine(root, "Main_V21", "Main_V21.ap21")), zap, true, Hash),
                (new JObject { ["state"] = "upgraded" }, demo, false, Hash), (null, demo, false, Hash) })
                Check.Equal(null, ProjectUpgradeRules.ReceiptProject(receipt, root, source, isArchive, sha!));
        }
        finally { Directory.Delete(examples, true); }
        return Task.CompletedTask;
    }

    internal static Task ConversionLogSummary()
    {
        const string log = "<LogFile projectName=\"Demo\"><MessageCell MessageClass=\"Information\" /><MessageCell MessageClass=\"Warning\" />" +
            "<MessageCell MessageClass=\"Warning\" /><MessageCell MessageClass=\"Error\" /><Text>ConversionInfoTabProjectSucceeded</Text></LogFile>";
        var summary = ProjectUpgradeRules.ConversionLog(log);
        Check.Equal(true, summary.Value<bool>("succeeded"));
        Check.Equal(1, summary.Value<int>("errors"));
        Check.Equal(2, summary.Value<int>("warnings"));
        Check.Equal(false, ProjectUpgradeRules.ConversionLog("<LogFile><Text>ConversionInfoTabLibrarySucceeded</Text></LogFile>").Value<bool>("succeeded"));
        return Task.CompletedTask;
    }

    internal static Task EvidenceNeverReplacesTheOutcome()
    {
        string root = Directory.CreateTempSubdirectory("tia-upgrade-evidence-").FullName;
        try
        {
            JObject Artifact(FileInfo file) => new() { ["path"] = file.FullName, ["bytes"] = file.Length };
            Directory.CreateDirectory(Path.Combine(root, "Demo", "Logs"));
            Directory.CreateDirectory(Path.Combine(root, "Demo_V21", "Logs"));
            File.WriteAllText(Path.Combine(root, "Demo", "Logs", "ConversionLog_15.0.0.0_to_16.0.0.0.xml"), "earlier upgrade");
            File.WriteAllText(Path.Combine(root, "Demo_V21", "Logs", "ConversionLog_16.0.0.0_to_21.0.0.0.xml"), "<Text>ConversionInfoTabProjectSucceeded</Text>");
            using (var big = File.Create(Path.Combine(root, "Demo", "Logs", "ConversionLog_16.0.0.0_to_21.0.0.0.xml"))) big.SetLength(16L * 1024 * 1024 + 1);
            File.WriteAllText(Path.Combine(root, "Demo_V21", "Demo_V21.ap21"), "x");
            var evidence = ProjectUpgradeRules.Evidence(root, Artifact);
            var logs = ((JArray)evidence["upgradeLogs"]!).OfType<JObject>().OrderBy(l => l.Value<long>("bytes")).ToArray();
            Check.Equal(2, logs.Length);
            Check.Equal(true, logs[0].Value<bool>("succeeded"));
            Check.Equal(false, logs[1].Value<bool>("parsed"));
            Check.Equal(null, logs[1]["succeeded"]);
            Check.Equal(1, ((JArray)evidence["upgradedProjects"]!).Count);
            Check.Equal(null, evidence["evidenceFailure"]);
            // A collection failure is recorded next to empty evidence instead of escaping.
            var failed = ProjectUpgradeRules.Evidence(Path.Combine(root, "missing"), Artifact);
            Check.Equal(true, failed.Value<string>("evidenceFailure")!.StartsWith("DirectoryNotFoundException", StringComparison.Ordinal));
            Check.Equal(0, ((JArray)failed["upgradeLogs"]!).Count);
            var throwing = ProjectUpgradeRules.Evidence(root, _ => throw new IOException("locked"));
            Check.Equal("IOException: locked", throwing.Value<string>("evidenceFailure"));
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
