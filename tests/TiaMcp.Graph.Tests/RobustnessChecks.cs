using TiaMcp.Graph;

/// <summary>
/// Seeded mutations of the GRAPH fixtures (authored and native V21 exports): GraphDocument.Parse and its report writers
/// return or refuse with a GraphException; no other exception, and every case returns.
/// </summary>
internal static class RobustnessChecks
{
    internal static string Run(Func<string, byte[]> fixture)
    {
        byte[][] sources = new[] { "sequence-v1.xml", "native-seed.xml", "native-populated-seed.xml", "native-rich-seed.xml", "native-alternative-seed.xml", "unknown-envelope.xml" }.Select(fixture).ToArray();
        var originals = sources.Select(GraphDocument.Parse).ToArray();
        var report = SeededMutations.Run("GRAPH XML", 20261125, 1000,
            random => { int index = random.Next(sources.Length); var (input, kind) = SeededMutations.Xml(sources[index], random); return (input, index + ":" + kind); },
            input => { var document = GraphDocument.Parse(input); _ = document.ToJson(); _ = document.ToHtml(); _ = originals[0].CompareTo(document); },
            error => (error as GraphException)?.Code, TimeSpan.FromSeconds(10));
        return report.Summary("GRAPH XML robustness");
    }
}
