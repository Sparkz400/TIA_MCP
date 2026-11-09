using TiaMcp.Openness.V21;
using EngineeringBridgeException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class CrossReferenceMemberFilterTests
{
    private static string? Match(string pattern, params string[] candidates) => new CrossReferenceMemberFilter(pattern).Match(candidates);

    internal static Task ArrayElementsMatchByConstantIndexAndRange()
    {
        Check.Equal("exact", Match("Real[210]", "\"FormatManagement\".Real[210]"));
        Check.Equal("exact", Match("Real[210]", "Real[210]"));
        Check.Equal("exact", Match("real[210]", "\"FormatManagement\".Real[210].x")); // deeper access to the element, case-insensitive
        Check.Equal(null, Match("Real[210]", "\"FormatManagement\".Real[209]"));
        Check.Equal(null, Match("Real[210]", "\"FormatManagement\".RealValues[210]"));
        Check.Equal("exact", Match("Real[200..220]", "\"FormatManagement\".Real[205]"));
        Check.Equal(null, Match("Real[200..220]", "\"FormatManagement\".Real[221]"));
        Check.Equal("exact", Match("Matrix[1..2, 5]", "\"DB\".Matrix[2, 5]"));
        Check.Equal(null, Match("Matrix[1..2, 5]", "\"DB\".Matrix[2, 6]"));
        return Task.CompletedTask;
    }

    internal static Task PossibleUsesAreReportedSeparately()
    {
        Check.Equal("indirect", Match("Real[210]", "\"FormatManagement\".Real[#i]"));
        Check.Equal("indirect", Match("Real[210]", "\"FormatManagement\".Real[\"Idx\".k + 1]"));
        Check.Equal("wholeArray", Match("Real[210]", "\"FormatManagement\".Real"));
        Check.Equal("container", Match("Station.Downholder.Enable", "\"DB3000\".Station"));
        Check.Equal("exact", Match("Station.Downholder", "#Station.Downholder.Enable"));
        Check.Equal("exact", Match("\"Station 3000\".Enable", "\"DB\".\"Station 3000\".Enable"));
        // The most specific kind wins across candidates; a slice ends the member path.
        Check.Equal("exact", Match("Real[210]", "\"FormatManagement\".Real[#i]", "\"FormatManagement\".Real[210]"));
        Check.Equal("exact", Match("Status", "\"DB\".Status.%X3"));
        Check.Equal(null, Match("Real[210]", "", "   "));
        return Task.CompletedTask;
    }

    internal static Task InvalidMemberPathsAreRefused()
    {
        foreach (var pattern in new[] { "", "Real[", "Real[a]", "Real[5..1]", "Real[1..2..3]", "Real]", new string('a', 513) })
        {
            try { _ = new CrossReferenceMemberFilter(pattern); }
            catch (EngineeringBridgeException error) { Check.Equal("InvalidRequest", error.Code); continue; }
            throw new InvalidOperationException("Accepted invalid memberPath: " + pattern);
        }
        Check.Equal(null, CrossReferenceMemberFilter.Parse("\"open"));
        Check.Equal(3, CrossReferenceMemberFilter.Parse("\"A.B\".C[1].D")!.Length);
        Check.Equal("A.B", CrossReferenceMemberFilter.Parse("\"A.B\".C[1].D")![0].Name);
        return Task.CompletedTask;
    }
}
