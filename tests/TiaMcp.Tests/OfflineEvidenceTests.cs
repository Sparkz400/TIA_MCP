using TiaMcp.Analysis;
using System.Xml.Linq;

namespace TiaMcp.Tests;

internal static class OfflineEvidenceTests
{
    internal static Task IoConflictsRespectBoundsAndUnknownOwners()
    {
        Check.Equal(true, OfflineEvidence.TryIoByteAddress("0", out int zero));
        Check.Equal(0, zero);
        Check.Equal(true, OfflineEvidence.TryIoByteAddress("2147483647", out _));
        foreach (string invalid in new[] { "-1", "2147483648", "1.0", "true", " 1", "1 " })
            Check.Equal(false, OfflineEvidence.TryIoByteAddress(invalid, out _));
        foreach (var size in new[] { (0, 0), (1, 1), (8, 1), (9, 2), (16, 2), (128, 16), (int.MaxValue, 268435456) })
            Check.Equal(size.Item2, OfflineEvidence.IoBitLengthToByteLength(size.Item1));
        // Native DI16 modules compiled at byte starts 0 and 2 with Length=16.
        Check.Equal(false, OfflineEvidence.IoByteRangesOverlap(0, OfflineEvidence.IoBitLengthToByteLength(16), 2, OfflineEvidence.IoBitLengthToByteLength(16)));
        Check.Equal(true, OfflineEvidence.IoByteRangesOverlap(0, OfflineEvidence.IoBitLengthToByteLength(16), 1, OfflineEvidence.IoBitLengthToByteLength(16)));
        Check.Equal(true, OfflineEvidence.IoAddressOverlapsNativeRange(OfflineEvidence.ParseIoAddress("%I1.7"), 0, 16));
        Check.Equal(false, OfflineEvidence.IoAddressOverlapsNativeRange(OfflineEvidence.ParseIoAddress("%I2.0"), 0, 16));
        Check.Equal(true, OfflineEvidence.IoAddressOverlapsNativeRange(OfflineEvidence.ParseIoAddress("%IW1"), 2, 16));
        Check.Equal(false, OfflineEvidence.IoAddressOverlapsNativeRange(OfflineEvidence.ParseIoAddress("%I0.7"), 0, 7));
        var before = new[] { new OfflineEvidence.IoRangeSnapshot { IoType = "Input", StartAddress = 0, LengthBits = 16 },
                             new OfflineEvidence.IoRangeSnapshot { IoType = "Output", StartAddress = 0, LengthBits = 16 } };
        var intended = new[] { new OfflineEvidence.IoRangeSnapshot { IoType = "Input", StartAddress = 32, LengthBits = 16 }, before[1] };
        Check.Equal(true, OfflineEvidence.IoAddressMutationPreserved(before, intended, "Input", 0, 32));
        Check.Equal(true, OfflineEvidence.IoAddressMutationPreserved(before, intended.Reverse(), "Input", 0, 32));
        var coupled = new[] { intended[0], new OfflineEvidence.IoRangeSnapshot { IoType = "Output", StartAddress = 32, LengthBits = 16 } };
        Check.Equal(false, OfflineEvidence.IoAddressMutationPreserved(before, coupled, "Input", 0, 32));
        Check.Equal(false, OfflineEvidence.IoAddressMutationPreserved(before, before, "Input", 0, 32));
        Check.Equal(false, OfflineEvidence.IoAddressMutationPreserved(before, [intended[0]], "Input", 0, 32));
        Check.Equal(false, OfflineEvidence.IoAddressMutationPreserved(before, [new OfflineEvidence.IoRangeSnapshot { IoType = "Input", StartAddress = 32, LengthBits = 8 }, before[1]], "Input", 0, 32));
        Check.Equal(false, OfflineEvidence.IoByteRangesOverlap(0, 4, 4, 2));
        Check.Equal(true, OfflineEvidence.IoByteRangesOverlap(0, 4, 3, 2));
        Check.Equal(true, OfflineEvidence.IoByteRangesOverlap(4, 2, 0, 8));
        Check.Equal(true, OfflineEvidence.IoByteRangesOverlap(int.MaxValue, 1, int.MaxValue - 2, 3));
        Check.Equal(true, OfflineEvidence.ValidIoByteRange(int.MaxValue, 1));
        Check.Equal(false, OfflineEvidence.ValidIoByteRange(int.MaxValue, 2));
        Check.Equal(false, OfflineEvidence.ValidIoByteRange(-1, 1));
        Check.Equal(false, OfflineEvidence.ValidIoByteRange(0, 0));
        Check.Equal(true, OfflineEvidence.IoOwnershipMayMatch([]));
        Check.Equal(true, OfflineEvidence.IoOwnershipMayMatch([null]));
        Check.Equal(true, OfflineEvidence.IoOwnershipMayMatch([false, null]));
        Check.Equal(true, OfflineEvidence.IoOwnershipMayMatch([false, true]));
        Check.Equal(false, OfflineEvidence.IoOwnershipMayMatch([false, false]));
        // Unknown controllers must stay visible as candidates, rather than being
        // silently classified as another PLC or a verified address association.
        Check.Equal("unresolved", OfflineEvidence.IoOwnershipResolution([]));
        Check.Equal("unresolved", OfflineEvidence.IoOwnershipResolution([null]));
        Check.Equal("unresolved", OfflineEvidence.IoOwnershipResolution([false, null]));
        Check.Equal("otherPlc", OfflineEvidence.IoOwnershipResolution([false, false]));
        Check.Equal("verified", OfflineEvidence.IoOwnershipResolution([true]));
        Check.Equal("verified", OfflineEvidence.IoOwnershipResolution([false, true, null]));
        return Task.CompletedTask;
    }

    internal static Task InterfaceSparseArraysAndNestedTypes()
    {
        // Shape captured from a native V21 UDT-backed DB export: nested UDT
        // sections differ from direct STRUCT members, and overrides are sparse.
        var xml = XDocument.Parse("""
            <Document><SW.Blocks.GlobalDB><AttributeList><Name>Data</Name><Namespace>N</Namespace><Interface>
            <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5"><Section Name="Static">
              <Member Name="Item" Datatype="&quot;SampleType&quot;"><Sections><Section Name="None">
                <Member Name="Enabled" Datatype="Bool"><StartValue>TRUE</StartValue></Member>
                <Member Name="Samples" Datatype="Array[-2..2] of Int">
                  <Subelement Path="2"><StartValue>23</StartValue></Subelement>
                  <Subelement Path="-2"><StartValue>-17</StartValue></Subelement>
                </Member>
                <Member Name="Meta" Datatype="Struct"><Member Name="CountValue" Datatype="DInt"><StartValue>5</StartValue></Member>
                  <Member Name="Label.Text" Datatype="String[32]"><DefaultValue>'a.b'</DefaultValue></Member></Member>
              </Section></Sections><UnknownNativeElement Important="true" /></Member>
            </Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
            """, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        string before = xml.ToString(SaveOptions.DisableFormatting);
        var normalized = OfflineEvidence.NormalizeInterface(xml, () => true, () => throw new Exception("Unexpected truncation"));
        var root = normalized.Sections.Single().Members.Single();
        Check.Equal("namedType", root.DeclarationKind);
        Check.Equal("SampleType", root.DeclaredType);
        Check.Equal("None", root.Members[0].Section);
        Check.Equal("TRUE", root.Members[0].StartValue);
        var array = root.Members[1];
        Check.Equal("array", array.DeclarationKind);
        Check.Equal(-2L, array.Array.Dimensions.Single().Lower);
        Check.Equal(2L, array.Array.Dimensions.Single().Upper);
        Check.Equal(2, array.ElementValues.Count);
        Check.Equal(2L, array.ElementValues[0].Indices.Single());
        Check.Equal("23", array.ElementValues[0].StartValue);
        Check.Equal(-2L, array.ElementValues[1].Indices.Single());
        Check.Equal<string?>(null, array.ElementValues[1].UnresolvedReason);
        Check.Equal(true, array.ElementValues[1].Xml.Contains("<StartValue>-17</StartValue>"));
        var nested = root.Members[2];
        Check.Equal("structure", nested.DeclarationKind);
        Check.Equal("Item|Meta|Label.Text", string.Join('|', nested.Members[1].Path));
        Check.Equal("'a.b'", nested.Members[1].DefaultValue);
        Check.Equal(true, nested.Members[0].XmlLine > 0);
        Check.Equal(before, xml.ToString(SaveOptions.DisableFormatting));
        return Task.CompletedTask;
    }

    internal static Task InterfaceInstanceDeclarationsAndSectionOwnership()
    {
        var xml = XDocument.Parse("""
            <Document><SW.Blocks.InstanceDB><AttributeList><Name>Instance</Name><Namespace>N</Namespace>
              <InstanceOfName>Controller</InstanceOfName><InstanceOfNumber>12</InstanceOfNumber>
              <Interface><Sections><Section Name="Static"><Member Name="First" Datatype="&quot;Cell&quot;">
                <Sections><Section Name="Input"><Member Name="Enable" Datatype="Bool" /></Section>
                  <Section Name="Output"><Member Name="Value" Datatype="DInt" /></Section><Section Name="InOut" /></Sections>
              </Member><Member Name="Second" Datatype="&quot;Cell&quot;"><Sections><Section Name="Input">
                <Member Name="Enable" Datatype="Bool" /></Section></Sections></Member></Section></Sections></Interface>
            </AttributeList></SW.Blocks.InstanceDB></Document>
            """);
        var normalized = OfflineEvidence.NormalizeInterface(xml, () => true, () => throw new Exception());
        var declaration = normalized.Declarations.Single();
        Check.Equal("Controller", declaration.InstanceOfName);
        Check.Equal("12", declaration.InstanceOfNumber);
        Check.Equal("N", declaration.Namespace);
        var section = normalized.Sections.Single();
        Check.Equal("Instance", section.OwnerName);
        Check.Equal("SW.Blocks.InstanceDB", section.OwnerKind);
        Check.Equal("multiInstance", section.Members[0].DeclarationKind);
        Check.Equal("Input|Output|InOut", string.Join('|', section.Members[0].Sections));
        Check.Equal("First|Enable", string.Join('|', section.Members[0].Members[0].Path));
        Check.Equal("Second|Enable", string.Join('|', section.Members[1].Members[0].Path));
        Check.Equal("Output", section.Members[0].Members[1].Section);
        return Task.CompletedTask;
    }

    internal static Task InterfaceArrayPathsStayExplicitWhenUnresolved()
    {
        var xml = XDocument.Parse("""
            <Document><SW.Types.PlcStruct><AttributeList><Name>MatrixType</Name><Interface><Sections><Section Name="None">
              <Member Name="Matrix" Datatype="Array[-1..1, 2..4] of Real">
                <Subelement Path="-1, 3"><StartValue>1.25</StartValue></Subelement>
                <Subelement Path="-2, 3"><StartValue>2</StartValue></Subelement>
                <Subelement Path="1"><StartValue>3</StartValue></Subelement>
                <Subelement Path="0,3.Member"><StartValue>4</StartValue><Opaque Keep="yes" /></Subelement>
              </Member>
              <Member Name="Symbolic" Datatype="Array[0..MAX] of Int"><Subelement Path="0"><StartValue>42</StartValue></Subelement></Member>
            </Section></Sections></Interface></AttributeList></SW.Types.PlcStruct></Document>
            """);
        var normalized = OfflineEvidence.NormalizeInterface(xml, () => true, () => throw new Exception());
        var members = normalized.Sections.Single().Members;
        Check.Equal(2, members[0].Array.Dimensions.Count);
        Check.Equal<string?>(null, members[0].ElementValues[0].UnresolvedReason);
        Check.Equal(true, members[0].ElementValues[1].UnresolvedReason.Contains("outside"));
        Check.Equal(true, members[0].ElementValues[2].UnresolvedReason.Contains("rank"));
        Check.Equal<long[]?>(null, members[0].ElementValues[3].Indices);
        Check.Equal("0,3.Member", members[0].ElementValues[3].NativePath);
        Check.Equal(true, members[0].ElementValues[3].Xml.Contains("Opaque"));
        Check.Equal(false, members[1].Array.BoundsResolved);
        Check.Equal(true, members[1].ElementValues[0].UnresolvedReason.Contains("bounds"));
        foreach (string invalid in new[] { "Array[2..1] of Int", "Array[0..999999999999999999999] of Int", "Array[*] of Int" })
            Check.Equal(false, OfflineEvidence.ParseArrayDeclaration(invalid).BoundsResolved);
        Check.Equal<OfflineEvidence.ArrayDeclaration?>(null, OfflineEvidence.ParseArrayDeclaration("Int"));
        var nestedArray = OfflineEvidence.ParseArrayDeclaration("Array[0..2] of Array[-1..3] of Int");
        Check.Equal("Int", nestedArray.ElementArray.ElementType);
        Check.Equal(-1L, nestedArray.ElementArray.Dimensions[0].Lower);
        return Task.CompletedTask;
    }

    internal static Task NativeSymbolsKeepNestedAndInstanceBoundaries()
    {
        const string first = "/projects/P/devices/S/items/C/plc/C/units/A/blocks/Instance";
        const string second = "/projects/P/devices/S/items/C/plc/C/units/B/blocks/Instance";
        bool Match(string query, string name = "\"Instance\".First.Values[-2].Enabled", string owner = first) =>
            OfflineEvidence.NativeSymbolMatches(query, "native:unique", name, "", "PLC\\Program blocks\\Instance", owner);
        Check.Equal(true, Match("Instance.First.Values[-2].Enabled"));
        Check.Equal(true, Match("\"Instance\".\"First\".Values[ -2 ].Enabled"));
        Check.Equal(true, Match(first + "::Instance.First.Values[-2].Enabled"));
        Check.Equal(false, Match(second + "::Instance.First.Values[-2].Enabled"));
        Check.Equal(false, Match("Instance.Second.Values[-2].Enabled"));
        Check.Equal(false, Match("Instance.First.Values[-1].Enabled"));
        Check.Equal(false, Match("Values[-2].Enabled"));
        Check.Equal(false, Match("Instance.First.Values[Index].Enabled"));
        Check.Equal(false, Match("Instance.First.Values[-2].Enabled", "\"Instance.First\".Values[-2].Enabled"));
        Check.Equal(false, Match("Instance.First.Values[-2].Enabled", "#Instance.First.Values[-2].Enabled"));
        Check.Equal(true, Match("#First.Enabled", "#\"First\".Enabled"));
        Check.Equal(false, Match("#first.Enabled", "#First.Enabled"));
        Check.Equal(false, Match(first + "::#First.Enabled", "#First.Enabled", null!));
        foreach (string malformed in new[] { "\"Instance", "Instance.", "Instance..First", "Instance.First.Values[]", "Instance.First.Values[9223372036854775808]" })
            Check.Equal(false, Match(malformed));
        return Task.CompletedTask;
    }

    internal static Task InterfaceNormalizationIsBounded()
    {
        var xml = XDocument.Parse("""
            <Document><SW.Blocks.GlobalDB><AttributeList><Interface><Sections><Section Name="Static">
              <Member Name="Values" Datatype="Array[0..2147483647] of Int">
                <Subelement Path="0"><StartValue>1</StartValue></Subelement><Subelement Path="1"><StartValue>2</StartValue></Subelement>
              </Member></Section></Sections></Interface></AttributeList></SW.Blocks.GlobalDB></Document>
            """);
        int used = 0;
        bool truncated = false;
        bool Take() { if (used >= 4) { truncated = true; return false; } used++; return true; }
        var normalized = OfflineEvidence.NormalizeInterface(xml, Take, () => truncated = true);
        Check.Equal(true, truncated);
        Check.Equal(4, used);
        Check.Equal(1, normalized.Sections[0].Members[0].ElementValues.Count);
        Check.Equal(2147483647L, normalized.Sections[0].Members[0].Array.Dimensions[0].Upper);
        // Native extension sections outside Interface must not be interpreted as declarations.
        var unrelated = XDocument.Parse("<Document><SW.Blocks.FC><AttributeList/><ObjectList><Sections><Section Name='Fake'><Member Name='Fake'/></Section></Sections></ObjectList></SW.Blocks.FC></Document>");
        Check.Equal(0, OfflineEvidence.NormalizeInterface(unrelated, () => true, () => { }).Sections.Count);
        return Task.CompletedTask;
    }

    internal static Task NativeCallsAndDirection()
    {
        // This direction is observed in the scratch FC's native UsedBy/Call record:
        // source is the FC, reference is the FB; the FB calls the FC.
        var source = "FC_IsEnabled";
        var target = "FB_EnableGate";
        int direction = OfflineEvidence.DependencyDirection("UsedBy");
        Check.Equal(target, direction > 0 ? source : target);
        Check.Equal(source, direction > 0 ? target : source);
        Check.Equal(1, OfflineEvidence.DependencyDirection("Uses"));
        Check.Equal(1, OfflineEvidence.DependencyDirection("InstanceType"));
        Check.Equal(-1, OfflineEvidence.DependencyDirection("TypeInstance"));
        foreach (var access in new[] { "Call", "UC", "CC" }) Check.Equal(true, OfflineEvidence.IsCallAccess(access));
        foreach (var access in new[] { "InstanceDB", "Multiinstance", "InstanceAndSymbol", "CreateReference", "Read", "Unknown", "call", "CALL FB 12" })
            Check.Equal(false, OfflineEvidence.IsCallAccess(access));
        foreach (var relation in new[] { "InstanceType", "TypeInstance", "OverlapsWith", "Assigns", "Undefined" })
            Check.Equal(false, OfflineEvidence.IsCallRelation(relation));
        Check.Equal(0, OfflineEvidence.DependencyDirection("OverlapsWith"));
        Check.Equal(0, OfflineEvidence.DependencyDirection("Undefined"));
        return Task.CompletedTask;
    }

    internal static Task NativeReadWriteClassification()
    {
        Check.Equal(true, OfflineEvidence.IsReadAccess("Read"));
        Check.Equal(false, OfflineEvidence.IsWriteAccess("Read"));
        Check.Equal(false, OfflineEvidence.IsReadAccess("Write"));
        Check.Equal(true, OfflineEvidence.IsWriteAccess("Write"));
        foreach (var value in new[] { "RW", "ReadWriteAndSymbol" })
        {
            Check.Equal(true, OfflineEvidence.IsReadAccess(value));
            Check.Equal(true, OfflineEvidence.IsWriteAccess(value));
        }
        foreach (var value in new[] { "Unknown", "Force", "Modify", "Call", "ReadSomething" })
        {
            Check.Equal(false, OfflineEvidence.IsReadAccess(value));
            Check.Equal(false, OfflineEvidence.IsWriteAccess(value));
        }
        return Task.CompletedTask;
    }

    internal static Task NativeIoAddressWidths()
    {
        foreach (var test in new[]
        {
            ("%I0.7", "Input", 7L, 1), ("%E2.0", "Input", 16L, 1), ("%QW64", "Output", 512L, 16),
            ("%AW8:P", "Output", 64L, 16), ("%IB10", "Input", 80L, 8), ("qd100", "Output", 800L, 32)
        })
        {
            var parsed = OfflineEvidence.ParseIoAddress(test.Item1)!;
            Check.Equal(test.Item2, parsed.Direction);
            Check.Equal(test.Item3, parsed.StartBit);
            Check.Equal(test.Item4, parsed.WidthBits);
        }
        foreach (var malformed in new[] { "", "%M1.0", "%DB1.DBX0.0", "%I0.8", "%IW0.1", "%Q-1", "%I2147483648", "%I9999999999999999999999999", "PumpInput", "I0 trailing" })
            Check.Equal<OfflineEvidence.IoAddress?>(null, OfflineEvidence.ParseIoAddress(malformed));
        return Task.CompletedTask;
    }

    internal static Task InstanceTargetsRespectCanonicalScopes()
    {
        const string plc = "/projects/P/groups/blocks/devices/Station/items/PLC/plc/PLC";
        string instance = plc + "/units/A/blocks/Instance";
        var own = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "", Path = plc + "/units/A/blocks/Pump" };
        var other = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "", Path = plc + "/units/B/blocks/Pump" };
        var valid = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", false, [own], []);
        Check.Equal(own.Path, valid.Path);
        Check.Equal(false, valid.NativeRelation);
        var wrongScope = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", false, [other], []);
        Check.Equal<string?>(null, wrongScope.Path);
        Check.Equal(true, wrongScope.Reason.Contains("another software scope"));
        return Task.CompletedTask;
    }

    internal static Task InstanceTargetsRejectIncompleteNameUniqueness()
    {
        const string plc = "/projects/P/devices/Station/items/PLC/plc/PLC";
        string instance = plc + "/blocks/Instance";
        var target = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "", Path = plc + "/blocks/Pump" };
        var truncated = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", true, [target], []);
        Check.Equal<string?>(null, truncated.Path);
        Check.Equal(true, truncated.Reason.Contains("truncated"));
        // A known native type edge remains authoritative even when unrelated blocks
        // were not indexed. This is distinct from name-based uniqueness.
        var exact = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", true, [target], [target.Path]);
        Check.Equal(target.Path, exact.Path);
        Check.Equal(true, exact.NativeRelation);
        return Task.CompletedTask;
    }

    internal static Task InstanceTargetsNeverGuessNamespaces()
    {
        const string plc = "/projects/P/devices/Station/items/PLC/plc/PLC";
        string instance = plc + "/units/A/blocks/Instance";
        var plain = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "", Path = plc + "/units/A/blocks/Pump" };
        var named = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "N", Path = plc + "/units/A/blocks/namespaces/N/Pump" };
        var duplicate = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", false, [plain, named], []);
        Check.Equal<string?>(null, duplicate.Path);
        Check.Equal(true, duplicate.Reason.Contains("ambiguous"));
        var qualified = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "Pump", false, [named], []);
        Check.Equal<string?>(null, qualified.Path);
        Check.Equal(true, qualified.Reason.Contains("namespaced"));
        var unknownFatherNamespace = OfflineEvidence.ResolveInstanceTarget(plc, instance, "N", "Pump", false, [plain], []);
        Check.Equal<string?>(null, unknownFatherNamespace.Path);
        var qualifiedString = OfflineEvidence.ResolveInstanceTarget(plc, instance, "", "N.Pump", false, [named], []);
        Check.Equal<string?>(null, qualifiedString.Path);
        return Task.CompletedTask;
    }

    internal static Task InstanceTargetsUseExactNativeTypeRelations()
    {
        const string plc = "/projects/P/devices/Station/items/PLC/plc/PLC";
        string instance = plc + "/units/A/blocks/namespaces/N/Instance";
        var first = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "N", Path = plc + "/units/A/blocks/namespaces/N/Pump" };
        var second = new OfflineEvidence.InstanceTarget { Name = "Pump", Namespace = "Other", Path = plc + "/units/B/blocks/namespaces/Other/Pump" };
        var exact = OfflineEvidence.ResolveInstanceTarget(plc, instance, "N", "Pump", false, [first, second], [second.Path, second.Path]);
        Check.Equal(second.Path, exact.Path);
        Check.Equal(true, exact.NativeRelation);
        var conflicting = OfflineEvidence.ResolveInstanceTarget(plc, instance, "N", "Pump", false, [first, second], [first.Path, second.Path]);
        Check.Equal<string?>(null, conflicting.Path);
        var absent = OfflineEvidence.ResolveInstanceTarget(plc, instance, "N", "Pump", false, [first], [second.Path]);
        Check.Equal<string?>(null, absent.Path);
        return Task.CompletedTask;
    }

    internal static Task NativeMemberKeysPreserveOwnerAndComponentBoundaries()
    {
        string firstOwner = "/projects/P/devices/S/items/C/plc/C/units/U/blocks/namespaces/A/Pump";
        string secondOwner = "/projects/P/devices/S/items/C/plc/C/units/U/blocks/namespaces/B/Pump";
        string first = OfflineEvidence.NativeMemberKey(firstOwner, "PLC", "Program blocks\\Pump", "Enabled", "#Enabled", "Bool");
        string second = OfflineEvidence.NativeMemberKey(secondOwner, "PLC", "Program blocks\\Pump", "Enabled", "#Enabled", "Bool");
        Check.Equal(false, first == second);
        Check.Equal(first, OfflineEvidence.NativeMemberKey(firstOwner, "PLC", "Program blocks\\Pump", "Enabled", "#Enabled", "Bool"));
        // Plain delimiter concatenation produces the same text for these different
        // device/path pairs; length-prefixed components must keep them distinct.
        string delimitedFirst = OfflineEvidence.NativeMemberKey(firstOwner, "A|B", "C", "Name", "", "Bool");
        string delimitedSecond = OfflineEvidence.NativeMemberKey(firstOwner, "A", "B|C", "Name", "", "Bool");
        Check.Equal(false, delimitedFirst == delimitedSecond);
        Check.Equal(false, OfflineEvidence.NativeMemberKey(null!, "PLC", "", "N", "", "T") ==
            OfflineEvidence.NativeMemberKey("", "PLC", "", "N", "", "T"));
        return Task.CompletedTask;
    }
}
