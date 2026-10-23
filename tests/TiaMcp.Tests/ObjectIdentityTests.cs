using TiaMcp.Model;

namespace TiaMcp.Tests;

internal static class ObjectIdentityTests
{
    private const string Plc = "/projects/Fixture/devices/Station/items/CPU/plc/PLC_1";

    internal static Task ExactPathsAndScopedNames()
    {
        string unit = Plc + "/units/Control";
        string path = unit + "/blocks/groups/Reusable/Enable";
        Check.Equal(true, ObjectIdentity.Matches(path, "Enable", unit, path, "enable", unit));
        Check.Equal(false, ObjectIdentity.Matches(path, "Enable", unit, path.ToLowerInvariant(), null!, unit));
        Check.Equal(false, ObjectIdentity.Matches(path, "Enable", unit, path, "Disable", unit));
        Check.Equal(false, ObjectIdentity.Matches(path, "Enable", unit, null!, "Enable", Plc));
        Check.Equal(false, ObjectIdentity.Matches(path, "Enable", unit, null!, "Enable", unit + "Extended"));
        return Task.CompletedTask;
    }

    internal static Task CanonicalPathScopes()
    {
        foreach (string scope in new[] { Plc, Plc + "/units/A%2FB", Plc + "/safetyUnits/Safe%20Logic" })
        {
            string path = scope + "/blocks/groups/Helpers/Enable";
            Check.Equal(Plc, ObjectIdentity.PlcPath(path));
            Check.Equal(scope, ObjectIdentity.ScopePath(Plc, path));
            Check.Equal(scope, ObjectIdentity.ScopePath(Plc, scope));
        }
        Check.Equal(Plc, ObjectIdentity.PlcPath(Plc));
        Check.Equal("project", ObjectIdentity.KindFromPath("/projects/Fixture"));
        Check.Equal("hardware", ObjectIdentity.KindFromPath("/projects/Fixture/devices/Station/items/CPU"));
        Check.Equal("plc", ObjectIdentity.KindFromPath(Plc));
        Check.Equal("softwareUnit", ObjectIdentity.KindFromPath(Plc + "/units/A%2FB"));
        Check.Equal("safetyUnit", ObjectIdentity.KindFromPath(Plc + "/safetyUnits/Safety"));
        Check.Equal("block", ObjectIdentity.KindFromPath(Plc + "/units/A/blocks/Enable"));
        string reservedNames = "/projects/plc/groups/plc/groups/items/devices/plc/items/plc/items/items/plc/plc";
        Check.Equal(reservedNames, ObjectIdentity.PlcPath(reservedNames + "/units/plc/blocks/groups/plc/Enable"));
        Check.Equal(reservedNames + "/units/plc", ObjectIdentity.ScopePath(reservedNames, reservedNames + "/units/plc/blocks/Enable"));
        Check.Equal("/projects/Fixture/ungrouped/plc/items/plc/plc/PLC", ObjectIdentity.PlcPath("/projects/Fixture/ungrouped/plc/items/plc/plc/PLC/blocks/Enable"));
        Check.Equal<string?>(null, ObjectIdentity.PlcPath("/projects/Fixture/devices/Station"));
        Check.Equal<string?>(null, ObjectIdentity.ScopePath(Plc, Plc + "Extended/blocks/Enable"));
        Check.Equal(Plc + "/units/A%2FB/blocks/groups/Helpers", ObjectIdentity.ParentPath(Plc + "/units/A%2FB/blocks/groups/Helpers/Enable"));
        string namespaced = ObjectIdentity.ObjectPath(Plc + "/units/A/blocks/groups/Helpers", "Enable", "Company.Area");
        Check.Equal(Plc + "/units/A/blocks/groups/Helpers/namespaces/Company.Area/Enable", namespaced);
        Check.Equal("Company.Area", ObjectIdentity.NamespaceFromPath(namespaced));
        Check.Equal(Plc + "/units/A/blocks/groups/Helpers", ObjectIdentity.ParentPath(namespaced));
        Check.Equal("", ObjectIdentity.NamespaceFromPath(Plc + "/blocks/groups/namespaces/Enable"));
        Check.Equal(Plc + "/blocks/Enable", ObjectIdentity.ObjectPath(Plc + "/blocks", "Enable", ""));
        Check.Equal("A/B", ObjectIdentity.NamespaceFromPath(ObjectIdentity.ObjectPath(Plc + "/units/A/types", "UDT", "A/B")));
        string plcNamedNamespaces = "/projects/P/devices/D/items/C/plc/namespaces";
        Check.Equal("", ObjectIdentity.NamespaceFromPath(plcNamedNamespaces + "/blocks/Gate"));
        Check.Equal(plcNamedNamespaces + "/blocks", ObjectIdentity.ParentPath(plcNamedNamespaces + "/blocks/Gate"));
        Check.Equal("", ObjectIdentity.NamespaceFromPath(Plc + "/units/namespaces/blocks/Gate"));
        Check.Equal(Plc + "/units/namespaces/blocks", ObjectIdentity.ParentPath(Plc + "/units/namespaces/blocks/Gate"));
        return Task.CompletedTask;
    }

    internal static Task DuplicateNamesRequireScope()
    {
        var fixtures = new[]
        {
            (Scope: Plc, Path: Plc + "/blocks/Enable"),
            (Scope: Plc + "/units/A", Path: Plc + "/units/A/blocks/Enable"),
            (Scope: Plc + "/units/B", Path: Plc + "/units/B/blocks/Enable")
        };
        Check.Equal(3, fixtures.Count(x => ObjectIdentity.Matches(x.Path, "Enable", x.Scope, null!, "Enable", null!)));
        Check.Equal(1, fixtures.Count(x => ObjectIdentity.Matches(x.Path, "Enable", x.Scope, null!, "Enable", Plc + "/units/B")));
        Check.Equal(1, fixtures.Count(x => ObjectIdentity.Matches(x.Path, "Enable", x.Scope, Plc + "/blocks/Enable", null!, null!)));
        return Task.CompletedTask;
    }

    internal static Task MutationScopeSelection()
    {
        string unit = Plc + "/units/A";
        Check.Equal(unit, ObjectIdentity.SingleScope(new[] { unit, unit }, null!, Plc));
        Check.Equal(Plc, ObjectIdentity.SingleScope(Array.Empty<string>(), null!, Plc));
        Check.Equal(unit, ObjectIdentity.SingleScope(Array.Empty<string>(), unit, Plc));
        ExpectRejected(() => ObjectIdentity.SingleScope(new[] { Plc, unit }, null!, Plc));
        ExpectRejected(() => ObjectIdentity.SingleScope(new[] { unit }, Plc, Plc));
        return Task.CompletedTask;
    }

    internal static Task GeneratedGroupBoundary()
    {
        Check.Equal(true, ObjectIdentity.IsGeneratedPath(Plc + "/blocks/systemGroups/Safety/Generated"));
        Check.Equal(true, ObjectIdentity.IsGeneratedPath(Plc + "/units/A/types/systemGroups/System/Udt"));
        Check.Equal(false, ObjectIdentity.IsGeneratedPath(Plc + "/blocks/groups/systemGroups/Normal"));
        Check.Equal(false, ObjectIdentity.IsGeneratedPath(Plc + "/blocks/groups/Name%2FsystemGroups%2F/Normal"));
        return Task.CompletedTask;
    }

    internal static Task NamespaceCaseAliasesCannotCreateTargets()
    {
        Check.Equal(true, ObjectIdentity.NamespaceCaseAlias("Company.Area", "company.area"));
        Check.Equal(true, ObjectIdentity.NamespaceCaseAlias("Company.Area", "Company.aREA"));
        Check.Equal(false, ObjectIdentity.NamespaceCaseAlias("Company.Area", "Company.Area"));
        Check.Equal(false, ObjectIdentity.NamespaceCaseAlias("Company.Area", "Company.Area.Other"));
        Check.Equal(false, ObjectIdentity.NamespaceCaseAlias("", "Company"));
        Check.Equal(false, ObjectIdentity.NamespaceCaseAlias(null!, ""));
        return Task.CompletedTask;
    }

    private static void ExpectRejected(Action action)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; }
        Check.Equal(true, rejected);
    }
}
