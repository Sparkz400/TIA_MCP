using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// Offline checks of SafetyDriveGuardRules against fakes: the guards the F-I/O hardware edits, drive writes, F-parameter writes,
// safety_drive_tags and the prepared-change cache share.
internal static class SafetyDriveGuardChecks
{
    private sealed class Node
    {
        internal string Name; internal bool Safety; internal Node Parent; internal List<Node> Children = new();
        internal Node Add(string name, bool safety = false) { var child = new Node { Name = name, Safety = safety, Parent = this }; Children.Add(child); return child; }
    }

    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
        void Refuse(Action action, string code, string label)
        {
            try { action(); }
            catch (BridgeException error) when (error.Code == code) { Check(true, label); return; }
            throw new InvalidOperationException("Expected " + code + ": " + label);
        }
        string FirstSafety(Node node) => SafetyHardwareRules.FirstSafety(node, n => n.Children, n => n.Parent, n => n.Safety, n => n.Name);

        // Safety hardware gate: the item, a subitem or an ancestor.
        var station = new Node { Name = "Station" };
        var plain = station.Add("DI");
        var fio = station.Add("F-DI"); var channel = fio.Add("Channel");
        var group = station.Add("Group"); group.Add("Inner").Add("F-DQ", true);
        fio.Safety = true;
        Check(FirstSafety(plain) == null, "a standard module is not safety hardware");
        Check(FirstSafety(fio) == "F-DI", "an F-I/O itself is safety hardware");
        Check(FirstSafety(channel) == "F-DI", "a subitem of an F-I/O sits below safety hardware");
        Check(FirstSafety(station) == "F-DI", "a station containing an F-I/O is refused");
        Check(FirstSafety(group) == "F-DQ", "a deeply nested F-I/O below a container is found");
        fio.Safety = false;
        Check(FirstSafety(station) == "F-DQ" && FirstSafety(fio) == null, "only the F-I/O that is safety counts");
        Refuse(() => SafetyHardwareRules.Require("F-DQ", "this hardware edit"), "SafetyBoundary", "a safety item refuses with SafetyBoundary");
        SafetyHardwareRules.Require(null, "this hardware edit"); count++; Console.WriteLine("PASS no safety item admits the edit");
        var deep = new Node { Name = "root" }; var tip = deep;
        for (int i = 0; i < 70; i++) tip = tip.Add("n" + i);
        Refuse(() => FirstSafety(deep), "TraversalLimit", "a subtree deeper than 64 refuses rather than passing unseen");
        Refuse(() => FirstSafety(tip), "TraversalLimit", "an ancestor chain deeper than 64 refuses");

        // Drive parameter block-list.
        foreach (int blocked in new[] { 9, 10, 15, 340, 802, 803, 804, 922, 970, 971, 972, 976, 977, 1900, 1910, 1960, 3900, 8920, 8923, 8925, 9300, 10000, 10999, 60022 })
            Check(DriveWriteRules.Protected(blocked), "p" + blocked + " is protected");
        // The parameters the evidence writes (p1082, p1120; motor entries p133, p304..p311, p322, p335, p601) stay writable.
        foreach (int open in new[] { 133, 304, 305, 307, 308, 310, 311, 322, 335, 601, 1082, 1120, 8919, 8926, 11000, 801, 805 })
            Check(!DriveWriteRules.Protected(open), "p" + open + " stays writable (verified or outside the block-list)");

        // Limits: only a provable violation refuses.
        DriveWriteRules.RequireWithinLimits("p1082", 12.5f, 0f, 210000f); count++; Console.WriteLine("PASS a value inside the limits is admitted (the verified p1082 write)");
        DriveWriteRules.RequireWithinLimits("p1", 0, 0, 10); DriveWriteRules.RequireWithinLimits("p1", 10, 0, 10); count++; Console.WriteLine("PASS the limits themselves are admitted");
        Refuse(() => DriveWriteRules.RequireWithinLimits("p1", 11, 0, 10), "InvalidRequest", "a value above the maximum refuses");
        Refuse(() => DriveWriteRules.RequireWithinLimits("p1", -1.5, 0f, 10f), "InvalidRequest", "a value below the minimum refuses");
        Refuse(() => DriveWriteRules.RequireWithinLimits("p1", float.NaN, 0f, 10f), "InvalidRequest", "NaN refuses");
        Refuse(() => DriveWriteRules.RequireWithinLimits("p1", double.PositiveInfinity, null, null), "InvalidRequest", "an infinite value refuses even without limits");
        DriveWriteRules.RequireWithinLimits("p1", 5000, null, null); count++; Console.WriteLine("PASS unreadable limits prove nothing");
        DriveWriteRules.RequireWithinLimits("p1", 5000, "0", "n/a"); count++; Console.WriteLine("PASS non-numeric limits prove nothing");
        DriveWriteRules.RequireWithinLimits("p1", true, 0, 0); DriveWriteRules.RequireWithinLimits("p1", "text", 0, 0); count++; Console.WriteLine("PASS booleans and text are not ranges");
        DriveWriteRules.RequireWithinLimits("p1", (ushort)7, 0, 10); count++; Console.WriteLine("PASS mixed numeric types compare by value");
        Refuse(() => DriveWriteRules.RequireWithinLimits("p1", (byte)200, 0f, 100.0), "InvalidRequest", "mixed numeric types refuse by value");

        // Limit lookup by number scans the drive once and stops when every number is found.
        int scanned = 0;
        IEnumerable<(int Number, string Name)> Parameters() { foreach (var p in new[] { (304, "p304[0]"), (305, "p305[0]"), (304, "p304[1]"), (999, "p999") }) { scanned++; yield return p; } }
        var found = DriveWriteRules.FirstPerNumber(Parameters(), p => p.Number, new HashSet<int> { 304, 305 });
        Check(found.Count == 2 && found[304].Name == "p304[0]" && scanned == 2, "the first parameter per number wins and the scan stops early");
        Check(DriveWriteRules.FirstPerNumber(Parameters(), p => p.Number, new HashSet<int> { 1 }).Count == 0, "a number without a parameter finds nothing");

        // Side effects: the diff of every parameter but the written ones.
        var before = new Dictionary<string, JToken> { ["p1082[0]"] = "a", ["p1083[0]"] = "b", ["r2"] = "c", ["p5"] = "d" };
        var after = new Dictionary<string, JToken> { ["p1082[0]"] = "A", ["p1083[0]"] = "B", ["r2"] = "c", ["p6"] = "new" };
        var effects = DriveWriteRules.SideEffects(before, after, new HashSet<string> { "p1082[0]" }, true);
        var changed = (JArray)effects["changed"];
        Check((int)effects["changedCount"] == 3 && changed.Count == 3 && (bool)effects["complete"], "side effects count recalculated, added and removed parameters");
        Check((string)changed[0]["name"] == "p1083[0]" && (string)changed[0]["change"] == "changed" && (string)changed[1]["name"] == "p5" && (string)changed[1]["change"] == "removed"
            && (string)changed[2]["name"] == "p6" && (string)changed[2]["change"] == "added", "side effects are ordered by name and classified");
        Check(changed.All(row => (string)row["name"] != "p1082[0]"), "the written parameter is not reported as a side effect");
        Check(!(bool)DriveWriteRules.SideEffects(before, after, new HashSet<string>(), false)["complete"], "an incomplete snapshot is flagged");
        var many = Enumerable.Range(0, 300).ToDictionary(i => "p" + i, i => (JToken)i);
        var moved = Enumerable.Range(0, 300).ToDictionary(i => "p" + i, i => (JToken)(i + 1));
        var capped = DriveWriteRules.SideEffects(many, moved, new HashSet<string>(), true);
        Check((int)capped["changedCount"] == 300 && ((JArray)capped["changed"]).Count == 200, "side effects are bounded and the total is kept");
        Check(((JArray)DriveWriteRules.SideEffects(many, many, new HashSet<string>(), true)["changed"]).Count == 0, "an unchanged drive reports no side effects");

        // Batch writes restore what they wrote when one fails.
        var state = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
        var oldValues = new Dictionary<string, int>(state); var newValues = new Dictionary<string, int> { ["a"] = 10, ["b"] = 20, ["c"] = 30 };
        var items = new List<string> { "a", "b", "c" };
        CompensatingWrites.Apply(items, key => state[key] = newValues[key], key => state[key] = oldValues[key], key => key);
        Check(state["a"] == 10 && state["b"] == 20 && state["c"] == 30, "a complete batch stays written");
        state = new Dictionary<string, int>(oldValues);
        Refuse(() => CompensatingWrites.Apply(items, key => { if (key == "c") throw new InvalidOperationException("out of range"); state[key] = newValues[key]; }, key => state[key] = oldValues[key], key => key),
            "MutationVerificationFailed", "a failing write refuses");
        Check(state["a"] == 1 && state["b"] == 2 && state["c"] == 3, "a failing write restores the earlier writes");
        state = new Dictionary<string, int>(oldValues);
        try
        {
            CompensatingWrites.Apply(items, key => { state[key] = newValues[key]; if (key == "b") throw new InvalidOperationException("applied then threw"); },
                key => { if (key == "a") throw new InvalidOperationException("stuck"); state[key] = oldValues[key]; }, key => key);
            throw new Exception("not refused");
        }
        catch (BridgeException error)
        {
            Check(error.Code == "MutationVerificationFailed" && error.Message.Contains("1 write(s) were restored") && error.Message.Contains("1 could not be restored (a)") && state["b"] == 2 && state["a"] == 10,
                "the failing setter is restored too and a failed restore is named");
        }

        // F-destination reuse.
        JToken Address(int value) => new JValue(value);
        var peers = new List<(string Path, JToken Current, bool PlannedSame)> { ("/A", Address(10), false), ("/B", Address(11), false) };
        Check(FDestinationRules.Duplicates(Address(12), peers, true).Count == 0, "a free F-destination address has no duplicate");
        Check(string.Join(",", FDestinationRules.Duplicates(Address(11), peers, true)) == "/B", "a used F-destination address names its F-I/O");
        Check(FDestinationRules.Duplicates(Address(12), new[] { ("/C", Address(5), true) }, true).Single() == "/C", "an address another write plans for the same value counts");
        Refuse(() => FDestinationRules.Duplicates(Address(1), new[] { ("/D", (JToken)null, false) }, true), "DependencyCoverageIncomplete", "an unreadable address on the F-source refuses");
        Check(FDestinationRules.Duplicates(Address(1), new[] { ("/D", (JToken)null, false), ("/E", (JToken)new JValue("x"), false), ("/F", Address(1), false) }, false).Single() == "/F",
            "an unreadable address elsewhere is skipped, a duplicate there is still found");

        // safety_drive_tags collisions over every scope.
        var names = new[] { "Tag1", "UnitConstant" };
        var tags = new[] { ("Tag1", "%I10.0"), ("UnitTag", "%IW20") };
        Check(SafetyTagCollisionRules.Find(names, tags, "New", "%I10.1") == null, "a free name and address are admitted");
        void Collides(string name, string address, string code, string tag, string label)
        {
            var found = SafetyTagCollisionRules.Find(names, tags, name, address);
            Check(found != null && found.Value.Code == code && found.Value.Tag == tag, label);
        }
        Collides("tag1", "%I11.0", "ObjectExists", null, "a name in the target scope collides, case-insensitively");
        Collides("unitconstant", "%I11.0", "ObjectExists", null, "a constant name collides as well");
        Collides("New", "%I10.0", "AddressInUse", "Tag1", "an address of an existing tag collides and names the tag");
        Collides("New", "%I20.3", "AddressInUse", "UnitTag", "an address inside a word of a tag in another scope collides");

        // Prepared-change eviction.
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        var ttl = TimeSpan.FromHours(12);
        string[] Evict(IEnumerable<(string, string, DateTime)> changes, int max) => PreparedChangeEviction.Select(changes, now, ttl, max).OrderBy(id => id).ToArray();
        var fresh = new[] { ("a", "prepared", now.AddHours(-1)), ("b", "prepared", now.AddHours(-2)) };
        Check(Evict(fresh, 64).Length == 0, "fresh prepared changes stay");
        Check(string.Join(",", Evict(new[] { ("old", "prepared", now.AddHours(-13)), ("new", "prepared", now.AddHours(-1)) }, 64)) == "old", "a prepared change past the TTL is evicted");
        Check(string.Join(",", Evict(new[] { ("a", "prepared", now.AddHours(-3)), ("b", "prepared", now.AddHours(-2)), ("c", "prepared", now.AddHours(-1)) }, 2)) == "a", "above the count the oldest prepared change is evicted");
        Check(Evict(new[] { ("x", "applied", now.AddDays(-9)), ("y", "applying", now.AddDays(-9)), ("z", "recovery_required", now.AddDays(-9)), ("r", "restored", now.AddDays(-9)), ("p", "preparing", now.AddDays(-9)) }, 0).Length == 0,
            "only prepared changes are ever evicted, however old");
        return count;
    }
}
