using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

internal static class HmiInternalTagCreationTests
{
    internal static void Run(Action<string, Action> test)
    {
        foreach (string label in new[] { "<Interne Variable>", "<Internal tag>", "", null })
        {
            string captured = label;
            test("internal tag preserves original label " + (captured ?? "null"), () =>
            {
                var f = new Fixture { Default = captured, Connection = captured };
                JObject result = f.Run();
                Equal(captured, result.Value<string>("defaultConnection"));
                Equal(captured, result.Value<string>("connection"));
                Equal(false, result.Value<bool>("nativeConnectionResolved"));
                Equal(false, result.Value<bool>("runtimeVerified"));
                Equal("Tag", result.Value<string>("name")); Equal("Bool", result.Value<string>("dataType"));
                Equal(string.IsNullOrEmpty(captured) ? 0 : 1, f.LookupCount);
                Equal("create,default,setType,name,type,connection,plcTag" + (string.IsNullOrEmpty(captured) ? "" : ",lookup"), string.Join(",", f.Calls));
                string saved = result.ToString(); f.Connection = "PLC"; f.Type = "Int";
                Equal(saved, result.ToString());
            });
        }
        test("localized label cannot change during type assignment", () => Reject(new Fixture { Connection = "<Internal tag>" }));
        test("null and empty defaults are not interchangeable", () => Reject(new Fixture { Default = null, Connection = "" }));
        test("a configured connection is never an internal sentinel", () => Reject(new Fixture { Resolves = true }));
        test("PLC binding refuses even with an unchanged label", () => Reject(new Fixture { PlcTag = "Motor.State" }));
        test("unexpected native data type refuses", () => Reject(new Fixture { Type = "Int" }));
        test("unexpected native name refuses", () => Reject(new Fixture { Name = "Other" }));
        foreach (string stage in new[] { "create", "default", "setType", "name", "type", "connection", "plcTag", "lookup" })
        {
            string captured = stage;
            test("internal tag failure at " + captured + " does not retry", () =>
            {
                var f = new Fixture { FailAt = captured };
                try { f.Run(); throw new Exception("Failure swallowed."); }
                catch (InvalidOperationException error) { Equal(captured, error.Message); }
                Equal(1, f.Calls.FindAll(call => call == "create").Count);
                Equal(captured, f.Calls[f.Calls.Count - 1]);
            });
        }
    }

    private static void Reject(Fixture f)
    {
        try { f.Run(); throw new Exception("Expected preservation refusal."); }
        catch (BridgeException error) { Equal("PreservationMismatch", error.Code); }
        Equal(1, f.Calls.FindAll(call => call == "create").Count);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual);
    }

    private sealed class Fixture
    {
        internal string Default = "<Interne Variable>", Connection = "<Interne Variable>", PlcTag = "", Type = "Bool", Name = "Tag", FailAt;
        internal bool Resolves;
        internal int LookupCount;
        internal readonly List<string> Calls = new List<string>();
        private int connectionReads;
        private void Call(string stage) { Calls.Add(stage); if (FailAt == stage) throw new InvalidOperationException(stage); }
        internal JObject Run() => HmiInternalTagCreation.Create("Tag", "Bool",
            name => { Call("create"); Equal("Tag", name); return this; },
            (tag, type) => { Call("setType"); Equal(this, tag); Equal("Bool", type); },
            tag => { Call("name"); return tag.Name; }, tag => { Call("type"); return tag.Type; },
            tag => { bool first = connectionReads++ == 0; Call(first ? "default" : "connection"); return first ? tag.Default : tag.Connection; },
            tag => { Call("plcTag"); return tag.PlcTag; },
            connection => { Call("lookup"); LookupCount++; Equal(Connection, connection); return Resolves; });
    }
}
