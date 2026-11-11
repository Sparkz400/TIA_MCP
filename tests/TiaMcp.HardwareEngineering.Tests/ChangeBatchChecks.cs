using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// payload.items on the simple single-object kinds: which kinds batch, the shape of a batch, and the cross-item refusals.
internal static class ChangeBatchChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        string Refused(Action action) { try { action(); } catch (BridgeException error) { return error.Code; } return null; }
        string Message(Action action) { try { action(); } catch (BridgeException error) { return error.Message; } return null; }
        static JObject Batch(params JObject[] items) => new JObject { ["items"] = new JArray(items.Cast<object>().ToArray()) };
        static JObject Create(string name) => new JObject { ["tagTableName"] = "Tags", ["name"] = name, ["dataTypeName"] = "Bool", ["logicalAddress"] = "%M1.0" };

        // Which kinds batch.
        foreach (string kind in ChangeBatchRules.Kinds)
            Check(ChangeBatchRules.IsBatch(kind, Batch(Create("A"))) && !ChangeBatchRules.IsBatch(kind, Create("A")), kind + " batches only with items");
        Check(!ChangeBatchRules.IsBatch("tag_set", Batch(Create("A"))), "tag_set keeps its own batch route");
        Check(!ChangeBatchRules.IsBatch("source_upsert", new JObject { ["source"] = "x" }) && !ChangeBatchRules.IsBatch("block_delete", new JObject { ["items"] = JValue.CreateNull() }),
              "a payload without items is never a batch");
        foreach (string kind in new[] { "block_delete", "type_delete", "group_delete", "source_upsert", "tag_table_import_xml", "hardware_attributes", "plc_compile" })
            Check(Refused(() => ChangeBatchRules.IsBatch(kind, Batch(Create("A")))) == "InvalidRequest", kind + " refuses items");

        // Shape.
        var members = ChangeBatchRules.Members(new JObject { ["scopePath"] = "/plc", ["items"] = new JArray(Create("A"), Create("B")) });
        Check(members.Length == 2 && members[0].Value<string>("name") == "A" && members.All(m => m.Value<string>("scopePath") == "/plc") && members.All(m => m["items"] == null),
              "each item becomes one payload of the kind and inherits the batch scopePath");
        var source = Batch(Create("A"));
        ChangeBatchRules.Members(source)[0]["name"] = "changed";
        Check(source["items"][0].Value<string>("name") == "A", "member payloads are copies");
        Check(Refused(() => ChangeBatchRules.Members(new JObject { ["items"] = new JArray() })) == "InvalidRequest"
              && Refused(() => ChangeBatchRules.Members(new JObject { ["items"] = "A" })) == "InvalidRequest", "an empty or non-array items refuses");
        Check(Refused(() => ChangeBatchRules.Members(Batch(Enumerable.Range(0, ChangeBatchRules.MaxItems + 1).Select(i => Create("T" + i)).ToArray()))) == "InvalidRequest"
              && ChangeBatchRules.Members(Batch(Enumerable.Range(0, ChangeBatchRules.MaxItems).Select(i => Create("T" + i)).ToArray())).Length == ChangeBatchRules.MaxItems, "the item count is bounded");
        var mixed = Batch(Create("A")); mixed["tagTableName"] = "Tags";
        Check(Refused(() => ChangeBatchRules.Members(mixed)) == "InvalidRequest", "only items and scopePath may stand beside items");
        Check(Refused(() => ChangeBatchRules.Members(new JObject { ["items"] = new JArray("A") })) == "InvalidRequest"
              && Refused(() => ChangeBatchRules.Members(Batch(new JObject()))) == "InvalidRequest", "an item must be a nonempty object");
        Check(Refused(() => ChangeBatchRules.Members(Batch(Batch(Create("A"))))) == "InvalidRequest", "an item cannot nest items");
        var otherScope = Create("A"); otherScope["scopePath"] = "/plc/units/U";
        Check(Refused(() => ChangeBatchRules.Members(new JObject { ["scopePath"] = "/plc", ["items"] = new JArray(otherScope) })) == "ScopeMismatch", "an item cannot name another scope than the batch");
        Check(Message(() => ChangeBatchRules.Members(Batch(Create("A"), new JObject()))).StartsWith("items[1] "), "a refusal names the item's index");

        // Names an item produces.
        Check(ChangeBatchRules.ProducedName("tag_create", Create("A")) == "A" && ChangeBatchRules.ProducedName("constant_create", Create("C")) == "C", "created tags and constants produce their name");
        Check(ChangeBatchRules.ProducedName("constant_set", new JObject { ["memberName"] = "C", ["attributes"] = new JObject { ["Name"] = "D" } }) == "D"
              && ChangeBatchRules.ProducedName("constant_set", new JObject { ["memberName"] = "C", ["attributes"] = new JObject { ["Value"] = "1" } }) == null, "a constant edit produces a name only when it renames");
        foreach (string kind in new[] { "block_rename", "type_rename", "tag_table_rename" })
            Check(ChangeBatchRules.ProducedName(kind, new JObject { ["newName"] = "N" }) == "N", kind + " produces newName");
        Check(ChangeBatchRules.ProducedName("tag_table_create", new JObject { ["tagTableName"] = "T" }) == "T", "a created tag table produces its name");
        foreach (string kind in new[] { "tag_delete", "constant_delete", "block_attributes_set" })
            Check(ChangeBatchRules.ProducedName(kind, new JObject { ["memberName"] = "A", ["newName"] = "ignored" }) == null, kind + " produces no name");

        // Cross-item refusals.
        Check(Refused(() => ChangeBatchRules.RequireDistinct(new[] { ("t/A", "A2"), ("t/B", "B2"), ((string)null, "C"), ("t/D", (string)null) })) == null, "distinct targets and names pass");
        Check(Refused(() => ChangeBatchRules.RequireDistinct(new[] { ("t/A", "A2"), ("T/a", "A3") })) == "InvalidRequest", "one object edited by two items refuses, case-insensitively");
        Check(Refused(() => ChangeBatchRules.RequireDistinct(new[] { ("t/A", "X"), ("t/B", "x") })) == "ObjectExists", "two items producing the same name refuse, case-insensitively");
        Check(Refused(() => ChangeBatchRules.RequireDistinct(new[] { ((string)null, "X"), ((string)null, "X") })) == "ObjectExists", "two created objects with one name refuse");
        Check(Refused(() => ChangeBatchRules.RequireDistinct(new[] { ((string)null, (string)null), ((string)null, (string)null) })) == null, "items without target or name never collide");
        Check(Message(() => ChangeBatchRules.RequireDistinct(new[] { ("t/A", "A2"), ("t/B", "B2"), ("t/A", "A3") })).StartsWith("items[0] and items[2] "), "a collision names both item indexes");
        return count;
    }
}
