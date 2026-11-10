using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// tag_set with payload.items: the shape of a batch and the refusals that need no project. Every one refuses the whole change.
internal static class TagBatchChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        string Refused(Action action) { try { action(); } catch (BridgeException error) { return error.Code; } return null; }
        string Message(Action action) { try { action(); } catch (BridgeException error) { return error.Message; } return null; }
        static JObject Edit(string member, string name = null, string address = null, string table = "Tags") =>
            new JObject { ["tagTableName"] = table, ["memberName"] = member, ["attributes"] = new JObject { ["Name"] = name, ["LogicalAddress"] = address }.WithoutNulls() };
        static JObject Batch(params JObject[] items) => new JObject { ["items"] = new JArray(items.Cast<object>().ToArray()) };

        // Shape.
        Check(!TagBatchRules.IsBatch(new JObject { ["tagTableName"] = "Tags", ["memberName"] = "A", ["attributes"] = new JObject { ["Name"] = "B" } })
              && !TagBatchRules.IsBatch(new JObject { ["items"] = JValue.CreateNull() }) && TagBatchRules.IsBatch(Batch(Edit("A", "B"))), "only a payload with items is a batch");
        var items = TagBatchRules.Items(Batch(Edit("A", "A2"), Edit("B", address: "%M1.0"), new JObject { ["tagTablePath"] = "/plc/tags/Other", ["memberName"] = "C", ["attributes"] = new JObject { ["DataTypeName"] = "Int" } }));
        Check(items.Length == 3 && items.Select(i => i.Index).SequenceEqual(new[] { 0, 1, 2 }) && items[0].NewName == "A2" && items[0].FinalName == "A2"
              && items[1].NewName == null && items[1].FinalName == "B" && items[2].TableName == null && items[2].TablePath == "/plc/tags/Other", "items keep their order, index, table selector and final name");
        Check(Refused(() => TagBatchRules.Items(new JObject { ["items"] = new JArray() })) == "InvalidRequest"
              && Refused(() => TagBatchRules.Items(new JObject { ["items"] = "A" })) == "InvalidRequest", "an empty or non-array items refuses");
        Check(Refused(() => TagBatchRules.Items(Batch(Enumerable.Range(0, TagBatchRules.MaxItems + 1).Select(i => Edit("T" + i, "N" + i)).ToArray()))) == "InvalidRequest"
              && TagBatchRules.Items(Batch(Enumerable.Range(0, TagBatchRules.MaxItems).Select(i => Edit("T" + i, "N" + i)).ToArray())).Length == TagBatchRules.MaxItems, "the item count is bounded");
        foreach (string field in new[] { "tagTableName", "tagTablePath", "memberName", "attributes" })
        {
            var mixed = Batch(Edit("A", "B"));
            mixed[field] = field == "attributes" ? new JObject { ["Name"] = "X" } : "X";
            Check(Refused(() => TagBatchRules.Items(mixed)) == "InvalidRequest", "items cannot be combined with the single-tag field " + field);
        }
        Check(Refused(() => TagBatchRules.Items(new JObject { ["items"] = new JArray("A") })) == "InvalidRequest", "a non-object item refuses");
        Check(Refused(() => TagBatchRules.Items(Batch(new JObject { ["memberName"] = "A", ["attributes"] = new JObject { ["Name"] = "B" } }))) == "InvalidRequest", "an item without a tag table refuses");
        Check(Refused(() => TagBatchRules.Items(Batch(new JObject { ["tagTableName"] = "Tags", ["attributes"] = new JObject { ["Name"] = "B" } }))) == "InvalidRequest", "an item without memberName refuses");
        Check(Refused(() => TagBatchRules.Items(Batch(new JObject { ["tagTableName"] = "Tags", ["memberName"] = "A" }))) == "InvalidRequest"
              && Refused(() => TagBatchRules.Items(Batch(new JObject { ["tagTableName"] = "Tags", ["memberName"] = "A", ["attributes"] = new JObject() }))) == "InvalidRequest", "an item without attributes refuses");
        var extra = Edit("A", "B"); extra["newName"] = "C";
        Check(Refused(() => TagBatchRules.Items(Batch(extra))) == "InvalidRequest", "an unknown item field refuses");
        Check(Refused(() => TagBatchRules.Items(Batch(new JObject { ["tagTableName"] = "Tags", ["memberName"] = "A", ["attributes"] = new JObject { ["ExternalWritable"] = "false" } }))) == "UnsupportedAttribute",
              "an attribute outside Name, DataTypeName and LogicalAddress refuses as in a single tag_set");
        Check(Refused(() => TagBatchRules.Items(Batch(new JObject { ["tagTableName"] = "Tags", ["memberName"] = "A", ["attributes"] = new JObject { ["Name"] = 5 } }))) == "InvalidRequest"
              && Refused(() => TagBatchRules.Items(Batch(Edit("A", " ")))) == "InvalidRequest" && Refused(() => TagBatchRules.Items(Batch(Edit("A", new string('n', 129))))) == "InvalidRequest",
              "attribute values are nonempty bounded strings");
        Check(Message(() => TagBatchRules.Items(Batch(Edit("A", "B"), Edit("C", "D"), new JObject { ["tagTableName"] = "Tags", ["memberName"] = "E" }))).StartsWith("items[2] "), "a refusal names the item's index");

        // Collisions: one refused item refuses the whole batch.
        var scope = new[] { "A", "B", "C", "Limit", "SystemConst" };
        void Plan(params JObject[] edits) => TagBatchRules.RequireNoCollisions(TagBatchRules.Items(Batch(edits)).Select(i => (i, "/plc/tags/" + i.TableName)), scope);
        Check(Refused(() => Plan(Edit("A", "A2"), Edit("B", "B2"), Edit("C", address: "%M2.0"))) == null, "distinct renames onto free names pass");
        Check(Refused(() => Plan(Edit("A", "a"), Edit("B", "B"))) == null, "a tag may change only its case or keep its name");
        Check(Refused(() => Plan(Edit("A", "A2"), Edit("B", "Limit"))) == "ObjectExists" && Refused(() => Plan(Edit("A", "systemconst"))) == "ObjectExists",
              "a rename onto an existing tag or constant name refuses, case-insensitively");
        Check(Refused(() => Plan(Edit("A", "X"), Edit("B", "x"))) == "ObjectExists", "two items requesting the same new name refuse");
        Check(Refused(() => Plan(Edit("A", "B"), Edit("B", "A"))) == "ObjectExists" && Refused(() => Plan(Edit("A", "B"), Edit("B", "B2"))) == "ObjectExists",
              "a swap or a chain onto a name another item renames away refuses");
        Check(Refused(() => Plan(Edit("A", "A2"), Edit("A", address: "%M3.0"))) == "InvalidRequest", "one tag edited by two items refuses");
        Check(Refused(() => Plan(Edit("A", "A2"), Edit("A", address: "%M3.0", table: "Other"))) == null, "the same member name in two tag tables is two tags");
        Check(Message(() => Plan(Edit("A", "A2"), Edit("B", "Limit"))).StartsWith("items[1]: "), "a collision names the item's index");
        return count;
    }

    private static JObject WithoutNulls(this JObject value)
    {
        foreach (var property in value.Properties().Where(p => (p.Value as JValue)?.Value == null).ToList()) property.Remove();
        return value;
    }
}
