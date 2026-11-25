using System.Xml;
using System.Xml.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

/// <summary>
/// Seeded mutations of native LAD/FBD block exports through the worker's block text reader (block_read): the networks,
/// the interface and the description are rendered, or the input is refused as XML or with a BridgeException. The reader
/// has no other refusal, so any other exception is a defect.
/// </summary>
internal static class GraphicalNetworkTextRobustnessTests
{
    internal static Task SeededMutationsAreRenderedOrRefused()
    {
        string? directory = null;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null && directory == null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "fixtures", "typed-graphical", "native", "6es7-513-1am03-0ab0-v4-1");
            if (Directory.Exists(path)) directory = path;
        }
        byte[][] sources = new[] { "Tgp_Boolean_LAD.xml", "Tgp_Boolean_FBD.xml", "Tgp_CTU_LAD.xml", "Tgp_FB_global_FBD.xml", "Tgp_Gt_LAD.xml", "Tgp_Eq_FBD.xml" }
            .Select(name => File.ReadAllBytes(Path.Combine(directory ?? throw new InvalidOperationException("Native graphical fixtures are missing."), name))).ToArray();
        static void Read(byte[] input)
        {
            // The worker's own export reader (block_read render:text), with its depth bound.
            var document = ExportedXml.Load(new MemoryStream(input, false));
            _ = GraphicalNetworkText.Format(GraphicalNetworkText.Render(document));
            _ = GraphicalNetworkText.RenderInterface(document);
            _ = GraphicalNetworkText.Describe(document, null, out _);
        }
        foreach (byte[] source in sources) Read(source);
        var report = SeededMutations.Run("block network text", 20261125, 2500,
            random => { int index = random.Next(sources.Length); var (input, kind) = SeededMutations.Xml(sources[index], random); return (input, index + ":" + kind); },
            Read, error => error is XmlException ? "XmlException" : error.GetType().Name == "BridgeException" ? "BridgeException" : null, TimeSpan.FromSeconds(10), KnownFailures, minimumOutcomes: 2);
        Console.WriteLine(report.Summary("Block network text robustness"));

        // Minimal reproducer of the listed known failure: the first wire end of a native LAD export loses its UId.
        var minimal = XDocument.Load(new MemoryStream(sources[0], false));
        minimal.Descendants().First(e => e.Name.LocalName == "IdentCon").Attribute("UId")!.Remove();
        // Fixed on 25 November: it used to raise ArgumentNullException; it is rendered as an unknown operand now.
        _ = GraphicalNetworkText.Render(minimal);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The worker's export reader refuses nesting beyond ExportedXml.MaxDepth with a BridgeException before XDocument.Load
    /// builds the tree (XDocument.Load alone takes about 2 s at 40 000 levels and does not return at 200 000). The deepest
    /// retained native export nests 19 levels.
    /// </summary>
    internal static Task ExportedXmlDepthIsBounded()
    {
        static byte[] Nested(int levels) => System.Text.Encoding.UTF8.GetBytes(
            "<Document>" + string.Concat(Enumerable.Repeat("<x>", levels - 1)) + string.Concat(Enumerable.Repeat("</x>", levels - 1)) + "</Document>");
        static TiaMcp.Openness.V21.BridgeException Refused(byte[] input)
        {
            try { ExportedXml.Load(new MemoryStream(input, false)); }
            catch (TiaMcp.Openness.V21.BridgeException error) { return error; }
            throw new Exception("A document nested deeper than " + ExportedXml.MaxDepth + " levels was read.");
        }
        Check.Equal(512, ExportedXml.MaxDepth);
        var refused = Refused(Nested(ExportedXml.MaxDepth + 2));
        Check.Equal("ExportedXmlTooDeep", refused.Code);
        Check.Equal(true, refused.Message.Contains("512"));
        Check.Equal(ExportedXml.MaxDepth + 1, ExportedXml.Load(new MemoryStream(Nested(ExportedXml.MaxDepth + 1), false)).Descendants().Count());
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Check.Equal("ExportedXmlTooDeep", Refused(Nested(200_000)).Code);
        Check.Equal(true, clock.Elapsed < TimeSpan.FromSeconds(5));

        string? deepest = null;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current != null && deepest == null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "fixtures", "typed-graphical", "native", "instance-effects-transitive-component", "after-Tgp_CompNestGrand.xml");
            if (File.Exists(path)) deepest = path;
        }
        var native = ExportedXml.Load(deepest ?? throw new InvalidOperationException("Native graphical fixtures are missing."));
        Check.Equal(true, native.Root!.DescendantsAndSelf().Max(e => e.Ancestors().Count()) + 1 >= 19);
        // DTDs stay prohibited, as before the bound.
        try { ExportedXml.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///secret'>]><x>&e;</x>"), false)); throw new Exception("A DTD was read."); }
        catch (XmlException) { }
        return Task.CompletedTask;
    }

    /// <summary>Known product defects in GraphicalNetworkText; empty when every mutation is rendered or refused.</summary>
    private static readonly SeededMutations.KnownFailure[] KnownFailures =
    [
    ];
}
