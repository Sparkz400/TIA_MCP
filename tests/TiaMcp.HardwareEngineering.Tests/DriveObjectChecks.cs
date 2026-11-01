using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// Drive object handles of drive_observe, drive_parameter_set and drive_telegram_set: the number when every object of a drive
// has a readable one, otherwise the container index for all of them.
internal static class DriveObjectChecks
{
    internal static int Run()
    {
        int count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); count++; Console.WriteLine("PASS " + label); }
        void Refuse(Action action, string code, string label, string text = null)
        {
            try { action(); }
            catch (BridgeException error) when (error.Code == code && (text == null || error.Message.Contains(text))) { Check(true, label); return; }
            throw new Exception("Expected " + code + ": " + label);
        }
        JObject Number(long value) => new JObject { ["hardwarePath"] = "/d", ["driveObjectNumber"] = value };
        JObject Index(long value) => new JObject { ["hardwarePath"] = "/d", ["driveObjectIndex"] = value };
        int?[] numbered = { 1, 2 }, unreadable = { null }, mixed = { 1, null };

        Check(!DriveObjectRules.ByIndex(numbered) && DriveObjectRules.ByIndex(unreadable) && DriveObjectRules.ByIndex(mixed), "a drive uses index handles when any number is unreadable");
        Check(DriveObjectRules.DriveId(false, 3, 0) == "DO3" && DriveObjectRules.DriveId(true, null, 0) == "DO@0" && DriveObjectRules.DriveId(true, 1, 1) == "DO@1",
            "driveId is DO<number> or DO@<index>, never mixed on one drive");

        Check(DriveObjectRules.Handle(Number(1)) == "driveObjectNumber" && DriveObjectRules.Handle(Index(0)) == "driveObjectIndex", "a payload names its one handle");
        Refuse(() => DriveObjectRules.Handle(new JObject { ["driveObjectNumber"] = 1, ["driveObjectIndex"] = 0 }), "InvalidRequest", "both handles refused", "exactly one");
        Refuse(() => DriveObjectRules.Handle(new JObject { ["hardwarePath"] = "/d" }), "InvalidRequest", "no handle refused", "exactly one");
        Refuse(() => DriveObjectRules.Handle(new JObject { ["driveObjectNumber"] = "1" }), "InvalidRequest", "a text number refused");
        foreach (var bad in new JToken[] { -1, (long)int.MaxValue + 1, "0", 0.5, JValue.CreateNull() })
            Refuse(() => DriveObjectRules.Handle(new JObject { ["driveObjectIndex"] = bad }), "InvalidRequest", "driveObjectIndex " + bad.ToString(Newtonsoft.Json.Formatting.None) + " refused");

        Check(DriveObjectRules.Resolve(Number(2), numbered) == 1, "a number resolves to its object's position");
        Refuse(() => DriveObjectRules.Resolve(Number(3), numbered), "DriveObjectNotFound", "an unknown number refused");
        Refuse(() => DriveObjectRules.Resolve(Number(2), new int?[] { 2, 2 }), "AmbiguousTarget", "a duplicate number refused");
        Refuse(() => DriveObjectRules.Resolve(Index(0), numbered), "DriveObjectHandleMismatch", "an index on a numbered drive says to use the number", "use driveObjectNumber");
        Check(DriveObjectRules.Resolve(Index(0), unreadable) == 0 && DriveObjectRules.Resolve(Index(1), mixed) == 1, "an index resolves on a drive with an unreadable number");
        Refuse(() => DriveObjectRules.Resolve(Number(1), unreadable), "DriveObjectHandleMismatch", "a number on an unreadable drive says to use the index", "use driveObjectIndex");
        Refuse(() => DriveObjectRules.Resolve(Number(1), mixed), "DriveObjectHandleMismatch", "a readable number on a mixed drive still needs the index", "use driveObjectIndex");
        Refuse(() => DriveObjectRules.Resolve(Index(1), unreadable), "DriveObjectNotFound", "an index past the container refused");

        // Prepare and apply re-resolve by the same payload; the identity joins the fingerprint, so any difference refuses apply.
        JObject Identity(int?[] numbers, int index = 0, string path = "/d", string order = "6SL3210-1KE21-3AF1", string firmware = "V4.7.14", int parameters = 4000)
            => DriveObjectRules.Identity(path, numbers, index, order, firmware, parameters);
        var prepared = Identity(unreadable);
        Check((string)prepared["driveId"] == "DO@0" && (int)prepared["driveObjectCount"] == 1 && (int)prepared["parameterCount"] == 4000, "the identity names the handle, container count and parameter count");
        Check(JToken.DeepEquals(prepared, Identity(unreadable)), "an unchanged drive object keeps its identity");
        foreach (var (changed, label) in new[] { (Identity(new int?[] { null, null }), "drive object count"), (Identity(unreadable, path: "/e"), "hardware path"),
                     (Identity(unreadable, order: "6SL3210-1KE21-3AF0"), "order number"), (Identity(unreadable, firmware: "V4.7.13"), "firmware"),
                     (Identity(unreadable, parameters: 3999), "parameter count"), (Identity(new int?[] { 1 }), "readable number") })
            Check(!JToken.DeepEquals(prepared, changed), "a changed " + label + " changes the identity");
        Check((string)Identity(numbered, 1)["driveId"] == "DO2", "a numbered object's identity keeps DO<number>");
        Refuse(() => DriveObjectRules.Resolve(Index(0), new int?[] { 1 }), "DriveObjectHandleMismatch", "an index payload refuses once the number became readable");
        return count;
    }
}
