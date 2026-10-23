using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

namespace TiaMcp.Tests;

internal static class TypedGraphicalTextBoundaryTests
{
    internal static Task CompleteIdentityTextIsRequired()
    {
        var fields = new (string Name, string Valid, int Maximum, Action<JObject, string> Set)[] {
            ("node key", "a", 128, (p,v) => p["model"]!["nodes"]![0]!["key"] = v),
            ("operand key", "in", 128, (p,v) => p["model"]!["operands"]![0]!["key"] = v),
            ("operand reference", "out", 128, (p,v) => p["model"]!["nodes"]![0]!["operand"] = v),
            ("connection node", "a", 128, (p,v) => p["model"]!["connections"]![0]!["to"]!["node"] = v),
            ("connection operand", "$in", 129, (p,v) => p["model"]!["connections"]![0]!["from"]!["node"] = v),
            ("insertion ID", "A0", 10, (p,v) => p["model"]!["insertAfterNetworkId"] = v),
            ("selected network ID", "1", 10, (p,v) => { Edit(p); p["model"]!["networkId"] = v; }),
            ("native UId", "21", 10, (p,v) => { Edit(p); p["model"]!["nodes"]![0]!["nativeUid"] = v; }),
            ("new block name", "NewBlock", 80, (p,v) => { Creation(p); p["model"]!["creation"]!["name"] = v; }),
            ("new interface name", "State", 128, (p,v) => { Creation(p); p["model"]!["creation"]!["interface"] = new JArray(new JObject {
                ["name"] = v, ["section"] = "Static", ["dataType"] = "Bool" }); }),
            ("new global instance", "State", 80, (p,v) => Instance(p, "createGlobal", v)),
            ("new multi instance", "State", 80, (p,v) => Instance(p, "createMulti", v)),
            ("instance key", "state", 128, (p,v) => { Instance(p, "createGlobal", "State"); p["instances"]![0]!["key"] = v; })
        };
        int checks = 0;
        foreach (var field in fields)
        {
            void Accept(string value)
            {
                var payload = Valid(); field.Set(payload, value);
                string before = payload.ToString(); TypedGraphicalPayload.Validate(payload);
                Check.Equal(before, payload.ToString()); checks++;
            }
            void Refuse(string value)
            {
                var payload = Valid(); field.Set(payload, value); string before = payload.ToString();
                try { TypedGraphicalPayload.Validate(payload); }
                catch (BridgeException error)
                {
                    Check.Equal("InvalidTypedGraphicalPayload", error.Code);
                    Check.Equal(before, payload.ToString()); checks++; return;
                }
                throw new InvalidOperationException("Accepted invalid complete text for " + field.Name);
            }
            Accept(field.Valid);
            // Raw payload grammar only: native integer range and graph associations are separate gates.
            string maximum = field.Maximum == 10 ? new string('1', 10)
                : field.Name == "connection operand" ? "$" + new string('a', 128) : new string('a', field.Maximum);
            Accept(maximum); Refuse(maximum + "a");
            foreach (string suffix in new[] { "\n", "\r", "\r\n", "\u2028", "\u2029", "\0", " " }) Refuse(field.Valid + suffix);
            Refuse("\n" + field.Valid); Refuse(field.Valid[..1] + "\n" + field.Valid[1..]);
        }
        Console.WriteLine($"PASS typed payload text boundaries: {checks} exact, maximum-length and malformed-text checks; no native calls.");
        return Task.CompletedTask;
    }

    private static JObject Valid() => JObject.Parse("""
        {"blockPath":"Devices/PLC/Program blocks/Independent","model":{"schemaVersion":1,"language":"FBD","operation":"insertNetwork","nodes":[{"key":"a","kind":"assign","operand":"out"}],"connections":[{"from":{"node":"$in","port":"value"},"to":{"node":"a","port":"in"}}],"operands":[{"key":"in","scope":"local","components":["Enable"]},{"key":"out","scope":"local","components":["Active"]}]}}
        """);
    private static void Edit(JObject p)
    {
        p["model"]!["operation"] = "editNetwork"; p["model"]!["networkId"] = "1";
        p["model"]!["connections"] = new JArray(); p["model"]!["nodes"]![0]!["nativeUid"] = "21";
    }
    private static void Creation(JObject p)
    {
        p.Remove("blockPath"); p["model"]!["operation"] = "createBlock";
        p["model"]!["creation"] = new JObject { ["name"] = "NewBlock", ["blockType"] = "FB", ["interface"] = new JArray() };
    }
    private static void Instance(JObject p, string kind, string name) => p["instances"] = new JArray(new JObject {
        ["key"] = "state", ["kind"] = kind, ["name"] = name });
}
