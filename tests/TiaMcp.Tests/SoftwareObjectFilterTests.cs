using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class SoftwareObjectFilterTests
{
    private const string Plc = "devices/PLC_1/items/PLC_1/plc/PLC_1";

    internal static Task GroupChainsMatchFromTheTopOfEachArea()
    {
        var filter = new SoftwareObjectFilter("Station 3000/Downholder", null);
        Check.Equal(true, filter.Active);
        Check.Equal(true, filter.Matches(Plc + "/blocks/groups/Station%203000/groups/Downholder/fc3002", "fc3002"));
        Check.Equal(true, filter.Matches(Plc + "/blocks/groups/Station%203000/groups/Downholder/groups/Sub/fc1", "fc1"));
        Check.Equal(true, filter.Matches(Plc + "/units/U1/tags/groups/Station%203000/groups/Downholder/Tags3000", "Tags3000"));
        Check.Equal(true, filter.Matches(Plc + "/blocks/groups/Station%203000/groups/Downholder/namespaces/N/fc9", "fc9"));
        Check.Equal(false, filter.Matches(Plc + "/blocks/groups/Station%203000/fc3000", "fc3000"));
        Check.Equal(false, filter.Matches(Plc + "/blocks/groups/Other/groups/Station%203000/groups/Downholder/fc1", "fc1"));
        Check.Equal(false, filter.Matches(Plc + "/blocks/groups/Station%203000/groups/DownholderOld/fc1", "fc1"));
        Check.Equal(true, filter.ContainsGroup(Plc + "/blocks/groups/Station%203000/groups/Downholder"));
        Check.Equal(false, filter.ContainsGroup(Plc + "/blocks/groups/Station%203000"));
        return Task.CompletedTask;
    }

    internal static Task GroupPathsHaveTheirOwnBound()
    {
        // namePrefix does not narrow groups: on a project with more groups than maxItems they used to take the whole item budget.
        var groups = Enumerable.Range(0, 100).Select(i => Plc + "/blocks/groups/G" + i).ToArray();
        int read = 0;
        IEnumerable<string> Source() { foreach (var path in groups) { read++; yield return path; } }
        var listing = new SoftwareGroupListing(new SoftwareObjectFilter(null, "Const"), includeGiven: false, maximum: 60);
        var first = listing.Take(Source(), out bool cut);
        Check.Equal(60, first.Count); Check.Equal(true, cut); Check.Equal(true, listing.Truncated); Check.Equal(61, read);
        Check.Equal(0, listing.Take(Source(), out cut).Count); Check.Equal(true, cut);
        Check.Equal(true, listing.Coverage.Contains("groupTruncation", StringComparison.Ordinal));
        // The bound is shared by all scopes of one read; a scope that fits is complete.
        var fits = new SoftwareGroupListing(new SoftwareObjectFilter("G1", null), false, 60);
        Check.Equal(1, fits.Take(groups, out cut).Count); Check.Equal(false, cut); Check.Equal(false, fits.Truncated);
        Check.Equal(false, fits.Coverage.Contains("groupTruncation", StringComparison.Ordinal));
        // include names the sections to read; group paths are not read at all then.
        read = 0;
        var omitted = new SoftwareGroupListing(new SoftwareObjectFilter(null, "Const"), includeGiven: true, maximum: 60);
        Check.Equal(false, omitted.Listed);
        Check.Equal(0, omitted.Take(Source(), out cut).Count); Check.Equal(false, cut); Check.Equal(0, read); Check.Equal(false, omitted.Truncated);
        Check.Equal(true, omitted.Coverage.StartsWith("Group paths are not listed when include is given", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    internal static Task FullGroupPathsAndNamePrefixes()
    {
        var path = new SoftwareObjectFilter(Plc + "/blocks/groups/Station%203000/", null);
        Check.Equal(true, path.Matches(Plc + "/blocks/groups/Station%203000/fc3000", "fc3000"));
        Check.Equal(false, path.Matches(Plc + "/blocks/groups/Station%2030000/fc1", "fc1"));
        Check.Equal(true, path.ContainsGroup(Plc + "/blocks/groups/Station%203000"));
        var names = new SoftwareObjectFilter(null, "db30");
        Check.Equal(true, names.Matches(Plc + "/blocks/DB3000", "DB3000"));
        Check.Equal(false, names.Matches(Plc + "/blocks/fc3000", "fc3000"));
        Check.Equal(true, names.ContainsGroup(Plc + "/blocks/groups/Anything"));
        var both = new SoftwareObjectFilter("Station 3000", "fc");
        Check.Equal(true, both.Matches(Plc + "/blocks/groups/Station%203000/fc3000", "fc3000"));
        Check.Equal(false, both.Matches(Plc + "/blocks/groups/Station%203000/db3000", "db3000"));
        Check.Equal(false, new SoftwareObjectFilter(null, null).Active);
        foreach (var (group, prefix) in new (string?, string?)[] { ("/", null), ("A//B", null), (null, ""), (new string('a', 1025), null) })
        {
            try { _ = new SoftwareObjectFilter(group, prefix); }
            catch (EngineeringBridgeException error) { Check.Equal("InvalidRequest", error.Code); continue; }
            throw new InvalidOperationException("Accepted invalid filter: " + group + " / " + prefix);
        }
        return Task.CompletedTask;
    }
}
