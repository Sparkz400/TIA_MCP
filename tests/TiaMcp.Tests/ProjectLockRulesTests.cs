using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class ProjectLockRulesTests
{
    private static ProjectLockRules.PortalView Portal(int id, string? project, string mode = "WithUserInterface") => new() { ProcessId = id, Mode = mode, ProjectPath = project! };

    // The Portal that has the project open is named; this worker's own Portal and other projects are not lock owners.
    internal static Task HoldersAreOtherPortalsWithTheSameProject()
    {
        var portals = new[] { Portal(10, @"C:\Projects\Line\Line.ap21"), Portal(11, @"c:\projects\line\LINE.ap21", "WithoutUserInterface"),
            Portal(12, @"C:\Projects\Other\Other.ap21"), Portal(13, null), Portal(20, @"C:\Projects\Line\Line.ap21") };
        var holders = ProjectLockRules.Holders(@"C:\Projects\Line\Line.ap21", portals, ownProcessId: 20);
        Check.Equal("10,11", string.Join(",", holders.Select(h => h.ProcessId)));
        Check.Equal(0, ProjectLockRules.Holders(@"C:\Projects\New\New.ap21", portals, 20).Count);
        Check.Equal(0, ProjectLockRules.Holders("", portals, 20).Count);
        var refusal = ProjectLockRules.Refusal(@"C:\Projects\Line\Line.ap21", holders);
        Check.Equal(2, ((JArray)refusal["lockOwners"]!).Count);
        Check.Equal(true, (bool)refusal["lockOwners"]![0]!["userInterface"]!);
        Check.Equal(false, (bool)refusal["lockOwners"]![1]!["userInterface"]!);
        Check.Equal(true, refusal.Value<string>("remediation")!.Contains("tia_attach to process 10"));
        var rows = ProjectLockRules.Portals(portals, ownProcessId: 20, ownsPortal: true);
        Check.Equal(5, rows.Count);
        Check.Equal(true, (bool)rows.Single(r => (int)r["processId"]! == 20)["ownedByThisWorker"]!);
        Check.Equal(false, (bool)rows.Single(r => (int)r["processId"]! == 10)["thisWorker"]!);
        return Task.CompletedTask;
    }
}
