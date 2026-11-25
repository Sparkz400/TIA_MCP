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
            // The worker reads TIA's own export without a depth bound. XDocument.Load itself slows superlinearly with nesting
            // (about 2 s at 40 000 levels, no return within 20 s at 200 000), so this loader refuses beyond the Host review
            // reader's 128 levels; the renderer is exercised on everything below that.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var depth = XmlReader.Create(new MemoryStream(input, false), settings))
                while (depth.Read()) if (depth.Depth > 128) throw new XmlException("More than 128 nested levels.");
            XDocument document;
            using (var reader = XmlReader.Create(new MemoryStream(input, false), settings)) document = XDocument.Load(reader);
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

    /// <summary>Known product defects in GraphicalNetworkText; empty when every mutation is rendered or refused.</summary>
    private static readonly SeededMutations.KnownFailure[] KnownFailures =
    [
    ];
}
