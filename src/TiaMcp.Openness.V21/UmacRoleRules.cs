#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Siemens-free planning for umac_role_set over its state snapshot:
    // { roles: [{ identifier, name, system, engineeringRights: [id], deviceRights: [{ device, identifier }] }],
    //   principals: [{ kind: projectUser|anonymous|umcUser|umcGroup, name, active, roles: [roleIdentifier] }] }.
    internal static class UmacRoleRules
    {
        internal static JObject Normalize(JObject state)
        {
            var roles = state["roles"].OfType<JObject>().ToArray(); var principals = state["principals"].OfType<JObject>().ToArray();
            if (roles.Where(r => r["identifier"].Type != JTokenType.Null).GroupBy(r => (string)r["identifier"], StringComparer.Ordinal).Any(g => g.Count() > 1) ||
                roles.GroupBy(r => (string)r["name"], StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1) ||
                principals.GroupBy(p => (string)p["kind"] + "\n" + (string)p["name"], StringComparer.Ordinal).Any(g => g.Count() > 1))
                throw new BridgeException("AmbiguousTarget", "UMAC role identifiers, role names or principal names are not unique.");
            foreach (var role in roles)
            {
                role["engineeringRights"] = new JArray(role["engineeringRights"].Values<string>().OrderBy(id => id, StringComparer.Ordinal));
                role["deviceRights"] = new JArray(role["deviceRights"].OrderBy(r => (string)r["device"], StringComparer.Ordinal).ThenBy(r => (string)r["identifier"], StringComparer.Ordinal));
            }
            foreach (var principal in principals) principal["roles"] = new JArray(principal["roles"].Values<string>().OrderBy(id => id, StringComparer.Ordinal));
            return new JObject { ["roles"] = new JArray(roles.OrderBy(r => r.Value<bool>("system")).ThenBy(r => (string)r["name"], StringComparer.Ordinal)),
                ["principals"] = new JArray(principals.OrderBy(p => (string)p["kind"], StringComparer.Ordinal).ThenBy(p => (string)p["name"], StringComparer.Ordinal)) };
        }

        // The exact state the native readback must equal. The caller has already refused system roles and resolved every right.
        internal static JObject Plan(JObject before, string action, string roleName, string userName, string[] engineeringRights, JObject[] deviceRights)
        {
            var after = (JObject)before.DeepClone();
            var roles = (JArray)after["roles"];
            var role = roles.OfType<JObject>().SingleOrDefault(r => !r.Value<bool>("system") && (string)r["name"] == roleName);
            if (action == "create")
            {
                if (roles.Any(r => string.Equals((string)r["name"], roleName, StringComparison.OrdinalIgnoreCase))) throw new BridgeException("ObjectExists", "A UMAC role with this name already exists.");
                roles.Add(new JObject { ["identifier"] = null, ["name"] = roleName, ["system"] = false, ["engineeringRights"] = new JArray(), ["deviceRights"] = new JArray() });
                return Normalize(after);
            }
            if (role == null) throw new BridgeException("ObjectNotFound", "No custom UMAC role has exactly this name.");
            if (action == "grant" || action == "revoke")
            {
                Edit((JArray)role["engineeringRights"], engineeringRights.Select(id => (JToken)id), action == "grant");
                Edit((JArray)role["deviceRights"], deviceRights, action == "grant");
            }
            else
            {
                var user = after["principals"].OfType<JObject>().SingleOrDefault(p => (string)p["kind"] == "projectUser" && (string)p["name"] == userName)
                    ?? throw new BridgeException("ObjectNotFound", "No project user other than the anonymous user has exactly this name.");
                Edit((JArray)user["roles"], new[] { role["identifier"] }, action == "assign");
            }
            return Normalize(after);
        }

        // Engineering function rights held through any role by at least one active project user; anonymous and UMC principals do not count.
        internal static string[] AdministeredRights(JObject state)
        {
            var rights = state["roles"].Where(r => r["identifier"].Type == JTokenType.String)
                .ToDictionary(r => (string)r["identifier"], r => r["engineeringRights"].Values<string>().ToArray(), StringComparer.Ordinal);
            return state["principals"].Where(p => (string)p["kind"] == "projectUser" && p.Value<bool>("active")).SelectMany(p => p["roles"].Values<string>())
                .SelectMany(id => rights.TryGetValue(id, out var held) ? held : new string[0]).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }

        // Which rights are administrative is not exposed by V21, so every engineering right an active project user holds stays held by one.
        internal static void RequireAdministratorPreserved(JObject before, JObject after)
        {
            var lost = AdministeredRights(before).Except(AdministeredRights(after), StringComparer.Ordinal).ToArray();
            if (lost.Length != 0) throw new BridgeException("AdministratorLockout", "The change would leave no active project user holding engineering function right(s) " + string.Join(", ", lost) + ". Grant them to another active user's role first.");
        }

        private static void Edit(JArray values, IEnumerable<JToken> changes, bool add)
        {
            foreach (var value in changes)
            {
                var existing = values.FirstOrDefault(v => JToken.DeepEquals(v, value));
                if (add == (existing != null)) throw new BridgeException(add ? "ObjectExists" : "ObjectNotFound", (add ? "Already assigned: " : "Not assigned: ") + value.ToString(Formatting.None));
                if (add) values.Add(value.DeepClone()); else existing.Remove();
            }
        }
    }
}
