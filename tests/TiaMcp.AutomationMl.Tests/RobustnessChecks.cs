using TiaMcp.Core;
using TiaMcp.Core.AutomationMl;

/// <summary>
/// Seeded mutations of retained and upstream CAEX documents: AutomationMlDocument.Parse returns a document (with
/// diagnostics) or refuses with a BridgeException; no other exception, and every case returns.
/// </summary>
internal static class RobustnessChecks
{
    internal static string Run()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "composite-engineering", "retained-v1"))) directory = directory.Parent;
        string retained = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("fixtures/composite-engineering is missing"), "fixtures", "composite-engineering");
        byte[][] sources = Directory.GetFiles(retained, "original.aml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Take(2)
            .Concat(new[] { "medium-valid-robot-cell.aml", "small-valid-motor.aml", "9_ExtInt_IntLink.aml", "5_SUC.aml" }.Select(name => Path.Combine(AppContext.BaseDirectory, "fixtures", "upstream", name)))
            .Select(File.ReadAllBytes).ToArray();
        if (sources.Length != 6) throw new InvalidOperationException("Expected two retained and four upstream AML fixtures.");
        foreach (byte[] source in sources) AutomationMlDocument.Parse(source);
        var report = SeededMutations.Run("AutomationML", 20261125, 3000,
            random => { int index = random.Next(sources.Length); var (input, kind) = SeededMutations.Xml(sources[index], random); return (input, index + ":" + kind); },
            input => { var document = AutomationMlDocument.Parse(input); _ = document.ToJson(); _ = document.CompareTo(document); },
            error => (error as BridgeException)?.Code, TimeSpan.FromSeconds(10));
        return report.Summary("AutomationML/CAEX robustness");
    }
}
