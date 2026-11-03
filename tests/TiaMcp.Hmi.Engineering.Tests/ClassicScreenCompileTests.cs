using Newtonsoft.Json.Linq;
using TiaMcp.Model;

static class ClassicScreenCompileTests
{
    static JObject Row(string description, string path, string state, int depth)
        => new() { ["description"] = description, ["path"] = path, ["state"] = state, ["depth"] = depth };
    static JObject Fixture(string stamp = "03.11.2026 10:08", int bytes = 2883484, string owner = "ControlA", string warning = "The process tag is missing.")
        => new() { ["errorCount"] = 0, ["warningCount"] = 1, ["targets"] = new JArray(new JObject {
            ["target"] = "/hmi/Runtime", ["errorCount"] = 0, ["warningCount"] = 1, ["messagesTruncated"] = false,
            ["messages"] = new JArray(Row("", "Runtime", "Warning", 0), Row($"Time stamp: {stamp} - {bytes} bytes used", "Runtime", "Warning", 1),
                Row("Software compilation started.", null, "Information", 2), Row("", owner, "Warning", 2), Row(warning, null, "Warning", 3),
                Row("Compilation completed: 0 errors, 1 warnings.", null, "Warning", 0)) }) };
    public static void Run(Action<string, Action> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("Classic diagnostic comparison assertion failed"); }
        check("Classic timestamp/byte-count wrapper is metadata, retained warning locations are significant", () => {
            var before = Fixture(); var copy = before.DeepClone();
            Assert(ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture("03.11.2026 10:10", 2883512)));
            Assert(JToken.DeepEquals(before, copy));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture(owner: "ControlB")));
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture(warning: "The position or size of the object is invalid.")));
            var count = Fixture(); count["warningCount"] = 2; Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, count));
            count = Fixture(); count["targets"][0]["warningCount"] = 2; Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, count));
        });
        check("Classic timestamp recognition refuses leaves, changed formats and incomplete diagnostic trees", () => {
            var before = Fixture();
            Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, Fixture("invalid", 2883512)));
            foreach (string damage in new[] { "leaf", "owner", "start", "truncated", "depth", "missingCount" }) {
                var other = Fixture("03.11.2026 10:10", 2883512); var target = other["targets"][0]; var messages = (JArray)target["messages"];
                if (damage == "leaf") messages[2]["depth"] = 1;
                if (damage == "owner") messages[1]["path"] = "Other";
                if (damage == "start") messages[2]["description"] = "Unexpected header";
                if (damage == "truncated") target["messagesTruncated"] = true;
                if (damage == "depth") messages[2]["depth"] = 6;
                if (damage == "missingCount") other.Remove("errorCount");
                Assert(!ClassicScreenCompileRules.ProblemsUnchanged(before, other));
            }
        });
    }
}
