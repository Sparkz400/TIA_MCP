using System.Xml.Linq;
using TiaMcp.Analysis;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class TypedGraphicalResolutionTests
{
    private sealed record Identity(string Path, string Name, string Namespace, string Scope, XDocument? Document = null);

    internal static Task ExactOwnersNeverFallBackToNames()
    {
        Identity[] owners = [new("/plc/tags/one/Signals", "Signals", "", "/plc"),
            new("/plc/tags/two/Signals", "Signals", "", "/plc"),
            new("/plc/blocks/one/State", "State", "", "/plc"),
            new("/plc/unit/blocks/State", "State", "", "/plc/unit")];
        foreach (var owner in owners)
            Check.Equal(owner, ExactOwner(owners, owner.Path));
        foreach (string wrong in new[] { "Signals", "/plc/tags/Signals", "/plc/tags/one/signals", "/plc/blocks/State" })
            Error("ExactTargetRequired", () => ExactOwner(owners, wrong));
        Error("ExactTargetRequired", () => ExactOwner(owners.Append(owners[0]), owners[0].Path));
        Error("ExactTargetRequired", () => ExactOwner(owners.Where(o => o.Scope == "/plc"), owners[3].Path));
        return Task.CompletedTask;
    }

    internal static Task SymbolBindingsRejectNewAmbiguity()
    {
        foreach (string symbol in new[] { "MotorReady", "MotorStateDB" })
        {
            var names = new List<string> { symbol, "Unrelated" };
            TypedGraphicalResolution.RequireUniqueSymbol(names, symbol);
            names.Add("AnotherUnrelated");
            TypedGraphicalResolution.RequireUniqueSymbol(names, symbol, "StaleDeclaration");
            // Even when the originally selected table/DB is unchanged, a second
            // emitted name makes the captured symbolic binding invalid.
            names.Add(symbol);
            Error("AmbiguousOperand", () => TypedGraphicalResolution.RequireUniqueSymbol(names, symbol));
            Error("StaleDeclaration", () => TypedGraphicalResolution.RequireUniqueSymbol(names, symbol, "StaleDeclaration"));
            names.RemoveAll(name => name == symbol);
            Error("StaleDeclaration", () => TypedGraphicalResolution.RequireUniqueSymbol(names, symbol, "StaleDeclaration"));
        }
        return Task.CompletedTask;
    }

    internal static Task TypesRequireExactNamespaceAndScope()
    {
        Identity[] types = [new("/plc/types/Flag", "Flag", "", "/plc"),
            new("/plc/types/A.Flag", "Flag", "A", "/plc"),
            new("/plc/types/B.Flag", "Flag", "B", "/plc"),
            new("/plc/unit/types/A.Flag", "Flag", "A", "/plc/unit")];
        Check.Equal(types[0], ResolveType(types, "Flag", "", "/plc"));
        Check.Equal(types[1], ResolveType(types, "Flag", "A", "/plc"));
        Check.Equal(types[2], ResolveType(types, "B.Flag", "A", "/plc"));
        Check.Equal(types[3], ResolveType(types, "Flag", "A", "/plc/unit"));
        Error("TypeUnresolved", () => ResolveType(types, "Flag", "Missing", "/plc"));
        Error("TypeUnresolved", () => ResolveType(types, "a.Flag", "", "/plc"));
        Error("TypeUnresolved", () => ResolveType(types, "B.Flag", "", "/plc/unit"));
        Error("TypeUnresolved", () => ResolveType(types.Append(types[1] with { Path = "/plc/types/other/A.Flag" }), "Flag", "A", "/plc"));
        return Task.CompletedTask;
    }

    internal static Task NestedUdtUsesDeclaringNamespaceEvenWithInlineMembers()
    {
        // Authored decision fixtures, not native reference qualification.
        var caller = Declaration("SW.Blocks.FC", "Caller", Member("State", "\"B.Outer\"",
            Member("Child", "\"Inner\"", Member("Flag", "DInt"))));
        var outer = Declaration("SW.Types.PlcStruct", "Outer", Member("Child", "\"Inner\"",
            Member("Flag", "Int")));
        var correct = Declaration("SW.Types.PlcStruct", "Inner", Member("Flag", "Bool"));
        var decoy = Declaration("SW.Types.PlcStruct", "Inner", Member("Flag", "Real"));
        Identity[] types = [new("/plc/types/B.Outer", "Outer", "B", "/plc", outer),
            new("/plc/types/B.Inner", "Inner", "B", "/plc", correct),
            new("/plc/types/A.Inner", "Inner", "A", "/plc", decoy)];
        var visited = new List<string>();
        var all = new[] { caller, outer, correct, decoy };
        var before = all.Select(Xml).ToArray();
        Tuple<string, XDocument> Load(string name, string ns)
        {
            var selected = ResolveType(types, name, ns, "/plc");
            visited.Add(selected.Path);
            return Tuple.Create(selected.Namespace, selected.Document!);
        }
        var resolved = TypedGraphicalResolution.ResolveMember(caller, ["State", "Child", "Flag"], "/plc/blocks/A.Caller", "A", Load);
        Check.Equal("Bool", resolved.DataType);
        Check.Equal("/plc/types/B.Outer|/plc/types/B.Inner", string.Join("|", visited));
        types = types.Where(type => type.Name != "Inner" || type.Namespace != "B").ToArray();
        Error("TypeUnresolved", () => TypedGraphicalResolution.ResolveMember(caller, ["State", "Child", "Flag"], "/caller", "A", Load));
        Check.Equal(string.Join("|", before), string.Join("|", all.Select(Xml)));
        return Task.CompletedTask;
    }

    internal static Task MemberPathsRejectAmbiguityArraysAndLiteralBoundaryConfusion()
    {
        var document = Declaration("SW.Blocks.FC", "Caller", Member("A.B", "Bool"),
            Member("A", "Struct", Member("B", "Int")), Member("Array", "Array[0..1] of Bool"),
            Member("Symbolic", "Array[LOW..HIGH] of Bool"), Member("Duplicate", "Bool"), Member("Duplicate", "Int"));
        var before = Xml(document);
        OfflineEvidence.InterfaceMember Resolve(params string[] path) => TypedGraphicalResolution.ResolveMember(document, path, "/caller", "", NoType);
        Check.Equal("Bool", Resolve("A.B").DataType);
        Check.Equal("Int", Resolve("A", "B").DataType);
        Error("OperandNotFound", () => Resolve("a", "B"));
        Error("OperandNotFound", () => Resolve("Duplicate"));
        Error("OperandNotFound", () => Resolve("A", "Absent"));
        Error("UnsupportedIndexing", () => Resolve("Array"));
        Error("UnresolvedArrayBounds", () => Resolve("Symbolic"));
        Error("InvalidOperand", () => Resolve());
        Check.Equal(before, Xml(document));
        return Task.CompletedTask;
    }

    internal static Task RetainedNativeDeclarationsRemainCompletelyUnchanged()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "fixtures", "typed-graphical"))) root = root.Parent;
        if (root == null) throw new InvalidOperationException("Repository fixtures not found.");
        foreach (var fixture in new[] { ("Tgp_TemplateFC_LAD.xml", "InputBool", "Bool"),
            ("Tgp_TemplateFC_FBD.xml", "OutputInt", "Int"), ("Tgp_InstanceEffect_DB.xml", "RetainedMarker", "Int") })
        {
            var document = EngineeringSources.ReadXml(File.ReadAllText(Path.Combine(root.FullName, "fixtures", "typed-graphical", "native", fixture.Item1)));
            var before = Xml(document);
            var selected = TypedGraphicalResolution.ResolveMember(document, [fixture.Item2], "/exact/" + fixture.Item1, "", NoType);
            Check.Equal(fixture.Item3, selected.DataType);
            Error("OperandNotFound", () => TypedGraphicalResolution.ResolveMember(document, ["DefinitelyMissing"], "/exact/" + fixture.Item1, "", NoType));
            Check.Equal(before, Xml(document));
            EngineeringSources.RequireNativePreservation(EngineeringSources.ReadXml(before), document);
        }
        return Task.CompletedTask;
    }

    private static Identity ExactOwner(IEnumerable<Identity> owners, string path) => TypedGraphicalResolution.Exact(owners,
        owner => owner.Path == path, "ExactTargetRequired", "Exact owner missing or ambiguous.");
    private static Identity ResolveType(IEnumerable<Identity> types, string name, string ns, string scope) =>
        TypedGraphicalResolution.ResolveType(types, name, ns, scope, type => type.Name, type => type.Namespace, type => type.Scope);
    private static Tuple<string, XDocument> NoType(string name, string ns) => throw new InvalidOperationException("Unexpected UDT lookup.");
    private static XElement Member(string name, string type, params XElement[] children) => new("Member", new XAttribute("Name", name), new XAttribute("Datatype", type), children);
    private static XDocument Declaration(string kind, string name, params XElement[] members) => new(new XElement("Document",
        new XElement(kind, new XAttribute("ID", "0"), new XElement("AttributeList", new XElement("Name", name),
            new XElement("Interface", new XElement("Sections", new XElement("Section", new XAttribute("Name", "Static"), members)))))));
    private static string Xml(XDocument document) => document.ToString(SaveOptions.DisableFormatting);
    private static void Error(string code, Action action)
    {
        try { action(); }
        catch (BridgeException error) { Check.Equal(code, error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
