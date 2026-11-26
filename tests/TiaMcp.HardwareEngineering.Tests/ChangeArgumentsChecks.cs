using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using TiaMcp.Openness.V21;

// Offline checks of ChangeArguments: the request checks and hashes every change kind of the worker shares.
internal static class ChangeArgumentsChecks
{
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
        JObject Args(string json) => JObject.Parse(json);

        // ChangeObject: an object, nothing else.
        Check((string)ChangeArguments.ChangeObject(Args("{\"payload\":{\"a\":1}}"), "payload")["a"] == "1", "ChangeObject returns the named object");
        Refuse(() => ChangeArguments.ChangeObject(Args("{}"), "payload"), "InvalidRequest", "ChangeObject refuses a missing member");
        Refuse(() => ChangeArguments.ChangeObject(Args("{\"payload\":[1]}"), "payload"), "InvalidRequest", "ChangeObject refuses an array");

        // ChangeStringArray: bounded count and length, nonempty strings; optional only when absent.
        Check(ChangeArguments.ChangeStringArray(Args("{}"), "names", 2, 5, optional: true).Length == 0, "An absent optional string array is empty");
        Check(ChangeArguments.ChangeStringArray(Args("{\"names\":[\"a\",\"bc\"]}"), "names", 2, 5).SequenceEqual(new[] { "a", "bc" }), "A bounded string array is returned in order");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{}"), "names", 2, 5), "InvalidRequest", "A required string array must be present");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":[]}"), "names", 2, 5), "InvalidRequest", "A required string array must not be empty");
        Check(ChangeArguments.ChangeStringArray(Args("{\"names\":[]}"), "names", 2, 5, optional: true).Length == 0, "An optional string array may be empty");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":[\"a\",\"b\",\"c\"]}"), "names", 2, 5), "InvalidRequest", "A string array over its count is refused");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":[\"abcdef\"]}"), "names", 2, 5), "InvalidRequest", "A string over its length is refused");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":[\" \"]}"), "names", 2, 5), "InvalidRequest", "A blank string is refused");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":[1]}"), "names", 2, 5), "InvalidRequest", "A number in a string array is refused");
        Refuse(() => ChangeArguments.ChangeStringArray(Args("{\"names\":null}"), "names", 2, 5), "InvalidRequest", "A null required string array is refused");

        // ChangeNames: 1-100 nonblank block names, unique without regard to case.
        Check(ChangeArguments.ChangeNames(Args("{\"blocks\":[\"Main\",\"FB_1\"]}"), "blocks").Length == 2, "Block names are returned");
        Refuse(() => ChangeArguments.ChangeNames(Args("{\"blocks\":[\"Main\",\"main\"]}"), "blocks"), "InvalidRequest", "Block names differing only in case are duplicates");
        Refuse(() => ChangeArguments.ChangeNames(Args("{\"blocks\":[]}"), "blocks"), "InvalidRequest", "No block name is refused");
        Refuse(() => ChangeArguments.ChangeNames(new JObject { ["blocks"] = new JArray(Enumerable.Range(0, 101).Select(i => "B" + i)) }, "blocks"), "InvalidRequest", "101 block names are refused");
        Check(ChangeArguments.ChangeNames(new JObject { ["blocks"] = new JArray(Enumerable.Range(0, 100).Select(i => "B" + i)) }, "blocks").Length == 100, "100 block names are accepted");
        Refuse(() => ChangeArguments.ChangeNames(Args("{\"blocks\":\"Main\"}"), "blocks"), "InvalidRequest", "A single string is not a block name array");
        Refuse(() => ChangeArguments.ChangeNames(Args("{\"blocks\":[\"\"]}"), "blocks"), "InvalidRequest", "An empty block name is refused");

        // Booleans must be explicit: a string or a missing member never counts as true or false.
        Check(ChangeArguments.ExplicitChangeBool(Args("{\"compile\":false}"), "compile") == false, "An explicit false is read");
        Check(ChangeArguments.ExplicitChangeBool(Args("{\"compile\":true}"), "compile"), "An explicit true is read");
        Refuse(() => ChangeArguments.ExplicitChangeBool(Args("{}"), "compile"), "InvalidRequest", "A missing boolean is refused");
        Refuse(() => ChangeArguments.ExplicitChangeBool(Args("{\"compile\":\"true\"}"), "compile"), "InvalidRequest", "A string boolean is refused");
        Refuse(() => ChangeArguments.ExplicitChangeBool(Args("{\"compile\":1}"), "compile"), "InvalidRequest", "A number boolean is refused");
        ChangeArguments.RequireChangeBool(Args("{\"confirm\":true}"), "confirm"); Check(true, "A required true passes");
        Refuse(() => ChangeArguments.RequireChangeBool(Args("{\"confirm\":false}"), "confirm"), "InvalidRequest", "A required boolean that is false is refused");

        // Hashes: lowercase hex SHA-256 of the UTF-8 text and of the file bytes; artifacts report path, size and hash.
        string Expected(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        Check(ChangeArguments.ChangeHash("Grüße") == Expected(Encoding.UTF8.GetBytes("Grüße")), "ChangeHash is the SHA-256 of the UTF-8 text");
        Check(ChangeArguments.ChangeHash("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "ChangeHash of empty text is the empty SHA-256");
        var file = Path.Combine(Directory.CreateTempSubdirectory("tiamcp-change-args-").FullName, "a.xml");
        try
        {
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'<', (byte)'a', (byte)'/', (byte)'>' };
            File.WriteAllBytes(file, bytes);
            Check(ChangeArguments.ChangeFileHash(file) == Expected(bytes), "ChangeFileHash hashes the file bytes, byte-order mark included");
            var artifact = ChangeArguments.ChangeArtifact(file);
            Check((string)artifact["path"] == file && (long)artifact["bytes"] == bytes.Length && (string)artifact["sha256"] == Expected(bytes), "ChangeArtifact reports path, size and hash");
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
        return count;
    }
}
