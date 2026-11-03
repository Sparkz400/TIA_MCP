// Compile-only signatures from installed public documentation. No entry point; never invoke these methods.
// Compilation establishes compatible references, not license availability or native qualification.
using System.IO;
#if PROBE_V19
using Siemens.Engineering;
internal static class ApiShapes
{
    private static void ProjectLifecycle(TiaPortal portal, DirectoryInfo directory, FileInfo file)
    {
        var created = portal.Projects.Create(directory, "OwnedProbe");
        created.Archive(directory, "OwnedProbe.zap19", ProjectArchivationMode.Compressed);
        created.Close();
        var reopened = portal.Projects.Open(file);
        reopened.Close();
    }
}
#else
using Siemens.Engineering.SW.FunctionCharts;
using Siemens.Engineering.MC.Drives.Dcc;
internal static class ApiShapes
{
    private static void Cfc(ChartProvider provider)
    {
        provider.CompleteExport("owned-export", "log", 0L, false);
        provider.SelectiveExport("owned-export", new[] { "OwnedChart" }, "log", 0L, false);
        provider.Import("owned-input", "log", 0L, false, false);
    }
    private static void Dcc(DriveControlChartComposition charts)
    {
        var chart = charts.Create("OwnedChart");
        chart.Export("owned-chart");
        charts.Export("owned-charts");
        charts.Import("owned-input", default(DccImportOptions));
    }
}
#endif
