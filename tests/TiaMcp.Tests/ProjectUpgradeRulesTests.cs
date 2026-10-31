using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class ProjectUpgradeRulesTests
{
    private static readonly string Hash = new('a', 64);

    private static async Task Refused(string code, string path, string? hash = null)
    {
        var failure = await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => ProjectUpgradeRules.SourceVersion(path, hash ?? Hash, out _)));
        Check.Equal(code, failure.Code);
    }

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

    internal static async Task LayoutStaysOwnedAndBounded()
    {
        string root = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, "tia-upgrade-" + Guid.NewGuid().ToString("N"), "u_12345678");
        var project = ProjectUpgradeRules.Plan(root, @"E:\Examples\Demo\Demo.ap16", false);
        Check.Equal(Path.Combine(root, "p", "Demo.ap16"), project.Input);
        Check.Equal(Path.Combine(root, "p"), project.Target);
        Check.Equal(Path.Combine(root, "upgrade-receipt.json"), project.Receipt);
        var archive = ProjectUpgradeRules.Plan(root, @"E:\Examples\Demo.zap14", true);
        Check.Equal(Path.Combine(root, "s", "Demo.zap14"), archive.Input);
        Check.Equal(Path.Combine(root, "w"), archive.Target);
        // A project folder is copied whole, so the destination may not lie inside it; an archive is copied alone.
        string folder = Path.GetDirectoryName(root)!;
        foreach (string inside in new[] { Path.Combine(folder, "Demo.ap16"), Path.Combine(root, "Demo.ap16") })
            Check.Equal("InvalidRequest", (await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => ProjectUpgradeRules.Plan(root, inside, false)))).Code);
        Check.Equal(Path.Combine(root, "s", "Demo.zap16"), ProjectUpgradeRules.Plan(root, Path.Combine(folder, "Demo.zap16"), true).Input);
        Check.Equal(Path.Combine(root, "p", "Demo.ap16"), ProjectUpgradeRules.Plan(root, Path.Combine(folder + "x", "Demo.ap16"), false).Input);
        // The upgraded <name>_V21 directory must fit TIA's 143-character limit.
        string stem = new('N', 143 - Path.Combine(root, "p").Length - 1 - "_V21".Length);
        ProjectUpgradeRules.Plan(root, @"E:\Examples\" + stem + ".ap16", false);
        Check.Equal("ProjectPathTooLong", (await Check.ThrowsAsync<EngineeringBridgeException>(() => Task.Run(() => ProjectUpgradeRules.Plan(root, @"E:\Examples\" + stem + "N.ap16", false)))).Code);
        Check.Equal(false, Directory.Exists(folder));
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
}
