using System.Xml.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class DependencyRulesTests
{
    internal static async Task DeletionPolicyAndEmptyGroupsFailClosed()
    {
        DependencyRules.RequirePolicy("rejectReferenced");
        foreach (string? policy in new[] { null, "", "cascade", "RejectReferenced", "ignore" })
            await Check.ThrowsAsync<ArgumentException>(() => { DependencyRules.RequirePolicy(policy!); return Task.CompletedTask; });
        DependencyRules.RequireEmpty(0, 0);
        foreach (var size in new[] { (1, 0), (0, 1), (2, 3), (-1, 0) })
            await Check.ThrowsAsync<InvalidOperationException>(() => { DependencyRules.RequireEmpty(size.Item1, size.Item2); return Task.CompletedTask; });
    }

    internal static Task NativeDependencyTokensAreConservative()
    {
        foreach (string xml in new[] {
            "<Document><Member Datatype='&quot;Pump&quot;' /></Document>",
            "<Document><Text><![CDATA[Pump.State := TRUE;]]></Text></Document>",
            "<Document><Comment>check pump before commissioning</Comment></Document>",
            "<Document><Unknown Reference='N.Pump' /></Document>" })
            Check.Equal(true, DependencyRules.Mentions(XDocument.Parse(xml), "Pump"));
        Check.Equal(false, DependencyRules.Mentions(XDocument.Parse("<Document><Text>PumpStation _Pump Pump2 ÄPump Pumpé</Text></Document>"), "Pump"));
        Check.Equal(true, DependencyRules.Mentions(XDocument.Parse("<Document><Text>\"A.B[2]\"</Text></Document>"), "A.B[2]"));
        Check.Equal(false, DependencyRules.Mentions(XDocument.Parse("<Document><Text>A-B2</Text></Document>"), "A.B[2]"));
        return Task.CompletedTask;
    }

    internal static async Task SelectedDeclarationExclusionPreservesOtherOwners()
    {
        const string first = "/projects/P/devices/S/items/C/plc/C/units/A/tags/Table";
        const string second = "/projects/P/devices/S/items/C/plc/C/units/B/tags/Table";
        var xml = XDocument.Parse("""
            <Document><SW.Tags.PlcTagTable><ObjectList>
              <SW.Tags.PlcUserConstant><AttributeList><Name>Limit</Name><Value>12</Value></AttributeList></SW.Tags.PlcUserConstant>
              <SW.Tags.PlcTag><AttributeList><Name>Other</Name><DataTypeName>Int</DataTypeName></AttributeList></SW.Tags.PlcTag>
            </ObjectList></SW.Tags.PlcTagTable></Document>
            """, LoadOptions.PreserveWhitespace);
        string original = xml.ToString(SaveOptions.DisableFormatting);
        var own = DependencyRules.WithoutSelectedDeclaration(xml, first, first, "SW.Tags.PlcUserConstant", "Limit");
        Check.Equal(false, DependencyRules.Mentions(own, "Limit"));
        Check.Equal(true, DependencyRules.Mentions(own, "Other"));
        var other = DependencyRules.WithoutSelectedDeclaration(xml, second, first, "SW.Tags.PlcUserConstant", "Limit");
        Check.Equal(true, DependencyRules.Mentions(other, "Limit"));
        Check.Equal(original, xml.ToString(SaveOptions.DisableFormatting));
        Check.Equal(original, other.ToString(SaveOptions.DisableFormatting));
        await Check.ThrowsAsync<InvalidOperationException>(() => Task.FromResult(DependencyRules.WithoutSelectedDeclaration(xml, first, first, "SW.Tags.PlcTag", "Limit")));
        await Check.ThrowsAsync<ArgumentException>(() => Task.FromResult(DependencyRules.WithoutSelectedDeclaration(xml, first, first, "SW.Blocks.FC", "Limit")));
        var referring = new XDocument(xml);
        referring.Descendants("DataTypeName").Single().Value = "Array[0..Limit] of Int";
        Check.Equal(true, DependencyRules.Mentions(DependencyRules.WithoutSelectedDeclaration(referring, first, first, "SW.Tags.PlcUserConstant", "Limit"), "Limit"));
    }
}
