namespace TiaMcp.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--probe-assets")
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(TiaMcp.Host.ProjectExplorerArtifacts.FindAssets()));
            return 0;
        }
        if (args.Length == 1 && args[0] == "--probe-typed-preview")
        {
            Console.WriteLine(ProjectModelTypedPreviewTests.AdaptedFixture().ToString(Newtonsoft.Json.Formatting.None));
            return 0;
        }
        Console.WriteLine($"MODEL REFERENCE os={System.Runtime.InteropServices.RuntimeInformation.OSDescription}; runtime={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; cpu={Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}; logicalProcessors={Environment.ProcessorCount}; utc={DateTime.UtcNow:O}");
        var tests = new (string Name, Func<Task> Run)[] {
            ("PLC library instance observations and saved retention", PlcLibraryInstanceRetentionTests.Run),
            ("Add-In detached selection identity and retention", AddInSelectionTests.Run),
            ("Hierarchy and exact selection", ProjectModelTests.HierarchyAndExactSelection),
            ("Cursor, lifetime and session binding", ProjectModelTests.CursorLifetimeAndSession),
            ("Invalid ancestry and budgets", ProjectModelTests.InvalidAncestryAndBudgets),
            ("Bounded graph and separate content freshness", ProjectModelTests.GraphsAndSeparateFreshness),
            ("Complete graph serialization remains bounded with unresolved evidence", ProjectModelTests.GraphSerializedByteLimit),
            ("Independent provider contracts", ProjectModelTests.ProviderContractIntegration),
            ("Native network, technology and unit relationship providers", ProjectModelNativeProviderTests.NetworkTechnologyAndUnitProviders),
            ("Every captured node kind declares its relationship scope", ProjectModelNativeProviderTests.EveryCapturedKindHasDeclaredScope),
            ("Typed preview preserves source, opacity and partial bounds", ProjectModelTypedPreviewTests.AdaptationAndBounds),
            ("Cached query performance", ProjectModelTests.CachedPerformance),
            ("Portable artifact manifest integrity and path ownership", ProjectExplorerArtifactTests.ManifestIntegrity),
            ("Portable HTML hostile labels remain inert and require no network", ProjectExplorerArtifactTests.InertEmbedding),
            ("Explorer asset discovery survives executable relocation", ProjectExplorerArtifactTests.RelocatedAssetDiscovery)
        };
        int failures = 0;
        foreach (var test in tests)
        {
            try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + error); }
        }
        Console.WriteLine($"{tests.Length - failures}/{tests.Length} project model checks passed.");
        return failures == 0 ? 0 : 1;
    }
}

internal static class Check
{
    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected <{expected}>, actual <{actual}>.");
    }
}
