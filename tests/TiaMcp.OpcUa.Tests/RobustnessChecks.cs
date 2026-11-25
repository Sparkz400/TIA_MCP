using TiaMcp.Core;
using TiaMcp.OpcUa;

/// <summary>
/// Seeded mutations of the retained NodeSet fixtures: OpcUaModelReview.Inspect and Compare return a report or refuse with
/// a BridgeException; no other exception, and every case returns.
/// </summary>
internal static class RobustnessChecks
{
    internal static string Run(Func<string, byte[]> fixture)
    {
        byte[][] sources = new[] { "machine-v1.xml", "machine-v1.1.xml", "machine-v2.xml", "server-interface-v1.xml", "server-interface-v2.xml", "server-interface-other.xml" }.Select(fixture).ToArray();
        foreach (byte[] source in sources) OpcUaModelReview.Inspect(source);
        var report = SeededMutations.Run("OPC UA NodeSet", 20261125, 3000,
            random => { int index = random.Next(sources.Length); var (input, kind) = SeededMutations.Xml(sources[index], random); return (input, index + ":" + kind); },
            input => { OpcUaModelReview.Inspect(input); OpcUaModelReview.Compare(sources[0], input); },
            error => (error as BridgeException)?.Code, TimeSpan.FromSeconds(10));
        return report.Summary("OPC UA NodeSet robustness");
    }
}
