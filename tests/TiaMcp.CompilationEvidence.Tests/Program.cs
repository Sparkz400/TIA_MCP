using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

int passed = 0, failed = 0;
void Check(string name, Action action) { try { action(); Console.WriteLine("PASS " + name); passed++; } catch (Exception e) { Console.Error.WriteLine("FAIL " + name + ": " + e); failed++; } }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed."); }
JObject Target() => new JObject { ["target"] = "/devices/HMI", ["state"] = "Success", ["errorCount"] = 0, ["warningCount"] = 0,
    ["messagesTruncated"] = false, ["messages"] = new JArray(new JObject { ["state"] = "Information", ["description"] = "Software compilation started.", ["depth"] = 0 }) };
JObject Aggregate(params JObject[] targets) => new JObject { ["errorCount"] = 0, ["targets"] = new JArray(targets) };

Check("scratch single-target envelope preserves flat diagnostics and independent copies", () => {
    var flat = Target(); flat.Remove("target"); var original = flat.DeepClone();
    var receipt = CompilationEvidenceRules.SingleTarget(flat, "/projects/Scratch/plc/PLC");
    Assert(CompilationEvidenceRules.IsSuccessful(receipt));
    Assert(CompilationEvidenceRules.IsSuccessfulTarget(receipt));
    Assert(JToken.DeepEquals(flat, original));
    Assert(receipt["targets"][0].Value<string>("target") == "/projects/Scratch/plc/PLC");
    Assert(JToken.DeepEquals(receipt["messages"], flat["messages"]));
    ((JArray)receipt["targets"][0]["messages"]).Add(new JObject { ["state"] = "Error" });
    Assert(!CompilationEvidenceRules.IsSuccessful(receipt));
    Assert(JToken.DeepEquals(flat, original));
    Assert(JToken.DeepEquals(receipt["messages"], flat["messages"]));
    foreach (string target in new[] { null, "", " " }) {
        bool refused = false; try { CompilationEvidenceRules.SingleTarget(flat, target); } catch (ArgumentException) { refused = true; }
        Assert(refused);
    }
});

Check("complete native Success, Information and warning-only results remain eligible", () => {
    var target = Target(); Assert(CompilationEvidenceRules.IsSuccessful(Aggregate(target)));
    target["state"] = "Information"; Assert(CompilationEvidenceRules.IsSuccessful(Aggregate(target)));
    target["state"] = "Warning"; target["warningCount"] = 1; ((JArray)target["messages"]).Add(new JObject { ["state"] = "Warning", ["depth"] = 1 });
    Assert(CompilationEvidenceRules.IsSuccessful(Aggregate(target))); Assert(CompilationEvidenceRules.IsSuccessfulTarget(target));
});
Check("observed Unified zero-error summary cannot mask password Error diagnostics", () => {
    var target = Target(); ((JArray)target["messages"]).Add(new JObject { ["state"] = "Error", ["description"] = "The entered password confirmation does not match the password. ", ["depth"] = 0 });
    var evidence = Aggregate(target); var before = evidence.DeepClone(); Assert(!CompilationEvidenceRules.IsSuccessful(evidence));
    Assert(JToken.DeepEquals(before, evidence)); Assert(target.Value<int>("errorCount") == 0);
});
Check("a zero count with an Error target state is rejected", () => {
    var target = Target(); target["state"] = "Error"; Assert(!CompilationEvidenceRules.IsSuccessful(Aggregate(target)));
});
Check("all affected targets require successful complete diagnostics", () => {
    var good = Target(); var bad = Target(); bad["target"] = "/devices/other"; bad["errorCount"] = 1;
    Assert(!CompilationEvidenceRules.IsSuccessful(Aggregate(good, bad)));
    bad["errorCount"] = 0; bad["messagesTruncated"] = true; Assert(!CompilationEvidenceRules.IsSuccessful(Aggregate(good, bad)));
});
Check("no aggregate can pass without nonempty target evidence", () => {
    foreach (JToken targets in new JToken[] { null, JValue.CreateNull(), new JObject(), new JArray(), new JArray(1) }) {
        var evidence = new JObject { ["errorCount"] = 0 }; if (targets != null) evidence["targets"] = targets;
        Assert(!CompilationEvidenceRules.IsSuccessful(evidence));
    }
    foreach (JToken identity in new JToken[] { null, JValue.CreateNull(), new JValue(""), new JValue(" "), new JValue(0) }) {
        var target = Target(); if (identity == null) target.Remove("target"); else target["target"] = identity;
        Assert(!CompilationEvidenceRules.IsSuccessful(Aggregate(target)));
    }
});
Check("missing or coerced counts cannot become zero-error success", () => {
    foreach (JToken value in new JToken[] { null, JValue.CreateNull(), new JValue(false), new JValue("0"), new JValue(0.0), new JValue(-1), new JValue(1) }) {
        var target = Target(); if (value == null) target.Remove("errorCount"); else target["errorCount"] = value;
        Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
        var evidence = Aggregate(Target()); if (value == null) evidence.Remove("errorCount"); else evidence["errorCount"] = value.DeepClone();
        Assert(!CompilationEvidenceRules.IsSuccessful(evidence));
    }
});
Check("truncation and absent diagnostic arrays reject otherwise successful summaries", () => {
    foreach (JToken value in new JToken[] { null, new JValue(true), new JValue("false"), JValue.CreateNull() }) {
        var target = Target(); if (value == null) target.Remove("messagesTruncated"); else target["messagesTruncated"] = value;
        Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
    }
    foreach (JToken value in new JToken[] { null, new JObject(), JValue.CreateNull(), new JArray(1) }) {
        var target = Target(); if (value == null) target.Remove("messages"); else target["messages"] = value;
        Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
    }
    var misspelled = Target(); misspelled["messageTruncated"] = false; misspelled.Remove("messagesTruncated"); Assert(!CompilationEvidenceRules.IsSuccessfulTarget(misspelled));
});
Check("nested and unknown diagnostic states cannot hide errors", () => {
    var target = Target(); target["messages"][0]["children"] = new JArray(new JObject { ["state"] = "Error", ["children"] = new JArray() });
    Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
    foreach (JToken state in new JToken[] { new JValue("Unknown"), JValue.CreateNull(), new JValue(0) }) {
        target = Target(); target["messages"][0]["state"] = state; Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
    }
    target = Target(); target["messages"][0]["children"] = JValue.CreateNull(); Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
});
Check("simulation's retained field names use the same admission rules", () => {
    var target = Target(); target["errors"] = target["errorCount"].DeepClone(); target.Remove("errorCount"); target["truncated"] = false; target.Remove("messagesTruncated");
    Assert(CompilationEvidenceRules.IsSuccessfulTarget(target, "errors", "truncated"));
    ((JArray)target["messages"]).Add(new JObject { ["state"] = "Error", ["depth"] = 2 });
    Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target, "errors", "truncated"));
});
Check("retained exception evidence never becomes compiler success", () => {
    var target = Target(); target["error"] = "native getter failed"; Assert(!CompilationEvidenceRules.IsSuccessfulTarget(target));
    var evidence = Aggregate(Target()); evidence["error"] = "native failure"; Assert(!CompilationEvidenceRules.IsSuccessful(evidence));
    Assert(!CompilationEvidenceRules.IsSuccessful(null)); Assert(!CompilationEvidenceRules.IsSuccessfulTarget(null));
});
if (args.Length == 1 && failed == 0)
{
    var flat = Target(); flat.Remove("target");
    File.WriteAllText(args[0], CompilationEvidenceRules.SingleTarget(flat, "/projects/Scratch/plc/PLC").ToString());
}
Console.WriteLine($"Compilation evidence: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
