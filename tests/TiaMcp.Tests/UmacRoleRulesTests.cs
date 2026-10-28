using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;
using UmacException = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests;

internal static class UmacRoleRulesTests
{
    private static JObject State() => UmacRoleRules.Normalize(JObject.Parse("""
        { "roles": [
            { "identifier": "sys-admin", "name": "Engineering administrator", "system": true, "engineeringRights": ["ManageUsers"], "deviceRights": [] },
            { "identifier": "ops", "name": "Ops", "system": false, "engineeringRights": ["OpenProject"], "deviceRights": [{ "device": "/projects/P/devices/PLC_1", "identifier": "FullAccess" }] } ],
          "principals": [
            { "kind": "projectUser", "name": "admin", "active": true, "roles": ["sys-admin"] },
            { "kind": "projectUser", "name": "op", "active": true, "roles": ["ops"] },
            { "kind": "projectUser", "name": "old", "active": false, "roles": [] },
            { "kind": "anonymous", "name": "Anonymous", "active": true, "roles": ["ops"] } ] }
        """));

    internal static async Task PlansExactEffectsAndPreservesAdministrators()
    {
        var before = State();
        var granted = UmacRoleRules.Plan(before, "grant", "Ops", null!, ["ManageUsers"], [new JObject { ["device"] = "/projects/P/devices/PLC_1", ["identifier"] = "Read" }]);
        Check.Equal("""["ManageUsers","OpenProject"]""", granted["roles"]![0]!["engineeringRights"]!.ToString(Newtonsoft.Json.Formatting.None));
        Check.Equal(2, granted["roles"]![0]!["deviceRights"]!.Count());
        UmacRoleRules.RequireAdministratorPreserved(before, granted);
        // An inactive user does not hold rights for administrator preservation.
        var assigned = UmacRoleRules.Plan(granted, "assign", "Ops", "old", [], []);
        Check.Equal(true, assigned["principals"]!.Any(p => (string?)p["name"] == "old" && p["roles"]!.Values<string>().Contains("ops")));
        Check.Equal(true, JToken.DeepEquals(before, UmacRoleRules.Plan(granted, "revoke", "Ops", null!, ["ManageUsers"], [new JObject { ["device"] = "/projects/P/devices/PLC_1", ["identifier"] = "Read" }])));
        var created = UmacRoleRules.Plan(before, "create", "Viewers", null!, [], []);
        Check.Equal(3, created["roles"]!.Count());
        UmacRoleRules.RequireAdministratorPreserved(before, created);

        await Code("AdministratorLockout", () => UmacRoleRules.RequireAdministratorPreserved(before, UmacRoleRules.Plan(before, "unassign", "Ops", "op", [], [])));
        await Code("AdministratorLockout", () => UmacRoleRules.RequireAdministratorPreserved(before, UmacRoleRules.Plan(before, "revoke", "Ops", null!, ["OpenProject"], [])));
        // op is the only active holder of OpenProject until admin also holds Ops.
        var shared = UmacRoleRules.Plan(granted, "assign", "Ops", "admin", [], []);
        UmacRoleRules.RequireAdministratorPreserved(shared, UmacRoleRules.Plan(shared, "unassign", "Ops", "op", [], []));
        await Code("ObjectExists", () => UmacRoleRules.Plan(before, "create", "ops", null!, [], []));
        await Code("ObjectExists", () => UmacRoleRules.Plan(before, "grant", "Ops", null!, ["OpenProject"], []));
        await Code("ObjectNotFound", () => UmacRoleRules.Plan(before, "revoke", "Ops", null!, ["ManageUsers"], []));
        await Code("ObjectNotFound", () => UmacRoleRules.Plan(before, "grant", "Engineering administrator", null!, ["OpenProject"], []));
        await Code("ObjectNotFound", () => UmacRoleRules.Plan(before, "assign", "Ops", "Anonymous", [], []));
        var duplicate = (JObject)before.DeepClone();
        ((JArray)duplicate["principals"]!).Add(new JObject { ["kind"] = "projectUser", ["name"] = "op", ["active"] = true, ["roles"] = new JArray() });
        await Code("AmbiguousTarget", () => UmacRoleRules.Normalize(duplicate));
    }

    private static async Task Code(string code, Action action)
    {
        var error = await Check.ThrowsAsync<UmacException>(() => { action(); return Task.CompletedTask; });
        Check.Equal(code, error.Code);
    }
}
