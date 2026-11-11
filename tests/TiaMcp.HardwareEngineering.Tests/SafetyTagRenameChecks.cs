using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// The one generic edit of a safety tag: a single tag_set with safetyAcknowledged=true and attributes holding only Name.
internal static class SafetyTagRenameChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        string Refused(Action action) { try { action(); } catch (BridgeException error) { return error.Code; } return null; }
        string Message(Action action) { try { action(); } catch (BridgeException error) { return error.Message; } return null; }
        static JObject Edit(JToken acknowledged, JObject attributes)
        {
            var payload = new JObject { ["tagTableName"] = "Tags", ["memberName"] = "Safe", ["attributes"] = attributes };
            if (acknowledged != null) payload["safetyAcknowledged"] = acknowledged;
            return payload;
        }
        static JObject Name(string value) => new JObject { ["Name"] = value };

        // Asking for the rename.
        Check(SafetyTagRenameRules.Requested("tag_set", Edit(true, Name("Safe2"))), "tag_set with safetyAcknowledged=true asks for a safety-tag rename");
        Check(!SafetyTagRenameRules.Requested("tag_set", Edit(null, Name("Safe2"))) && !SafetyTagRenameRules.Requested("tag_delete", Edit(null, Name("Safe2")))
              && !SafetyTagRenameRules.Requested("tag_set", null), "without the flag nothing is asked for");
        foreach (JToken value in new JToken[] { false, "true", 1, JValue.CreateNull() })
            Check(Refused(() => SafetyTagRenameRules.Requested("tag_set", Edit(value, Name("Safe2")))) == "InvalidRequest", "safetyAcknowledged " + value.ToString(Newtonsoft.Json.Formatting.None) + " is refused");
        foreach (string kind in new[] { "tag_delete", "tag_create", "constant_set", "constant_delete" })
            Check(Refused(() => SafetyTagRenameRules.Requested(kind, Edit(true, Name("Safe2")))) == "InvalidRequest", kind + " does not take safetyAcknowledged");

        // Name and nothing else.
        Check(Refused(() => SafetyTagRenameRules.RequireNameOnly(Name("Safe2"))) == null, "attributes holding only Name pass");
        foreach (var attributes in new[] { new JObject { ["LogicalAddress"] = "%I0.0" }, new JObject { ["DataTypeName"] = "Bool" },
                     new JObject { ["Name"] = "Safe2", ["LogicalAddress"] = "%I0.0" }, new JObject { ["Name"] = "Safe2", ["DataTypeName"] = "Bool" }, new JObject(), null })
            Check(Refused(() => SafetyTagRenameRules.RequireNameOnly(attributes)) == "SafetyBoundary", "a safety tag refuses attributes " + (attributes?.ToString(Newtonsoft.Json.Formatting.None) ?? "null"));
        Check(Refused(() => SafetyTagRenameRules.RequireNameOnly(Name(" "))) == "SafetyBoundary" && Refused(() => SafetyTagRenameRules.RequireNameOnly(new JObject { ["Name"] = 5 })) == "SafetyBoundary",
              "the new Name must be a nonempty string");
        Check(Message(() => SafetyTagRenameRules.RequireNameOnly(new JObject { ["Name"] = "Safe2", ["LogicalAddress"] = "%I0.0" })).Contains("LogicalAddress stays excluded"), "the refusal names the excluded attribute");

        // A batch never takes the flag: beside items, or inside an item.
        var item = new JObject { ["tagTableName"] = "Tags", ["memberName"] = "Safe", ["attributes"] = Name("Safe2") };
        Check(Refused(() => TagBatchRules.Items(new JObject { ["items"] = new JArray(item.DeepClone()), ["safetyAcknowledged"] = true })) == "InvalidRequest", "a tag_set batch refuses safetyAcknowledged beside items");
        var flagged = (JObject)item.DeepClone(); flagged["safetyAcknowledged"] = true;
        Check(Refused(() => TagBatchRules.Items(new JObject { ["items"] = new JArray(flagged) })) == "InvalidRequest", "a tag_set batch refuses safetyAcknowledged inside an item");
        return count;
    }
}
