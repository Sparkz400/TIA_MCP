using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Core.EngineeringJobs;

// RFC 8785: the shared vectors (the net48 worker reproduces them in TiaMcp.ComponentQualification.Boundary.Tests),
// the number writer against .NET's own shortest round trip, and hashes stored by builds before the change.
internal static class CanonicalJsonChecks
{
    internal static void Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
        void Reject(Action action, string code, string name)
        { try { action(); } catch (BridgeException error) when (error.Code == code) { passed++; return; } throw new Exception(name); }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fixtures", "canonical-json", "vectors.v1.json"))) directory = directory.Parent;
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "fixtures", "canonical-json", "vectors.v1.json")))!;
        foreach (var vector in vectors["cases"]!.AsArray())
        {
            string name = vector!["name"]!.GetValue<string>(); string? refusal = vector["refusal"]?.GetValue<string>();
            JsonNode input;
            if (vector["utf16"] is JsonArray units)
            {
                string text = new(units.Select(u => (char)u!.GetValue<int>()).ToArray());
                input = vector["as"]!.GetValue<string>() == "key" ? new JsonObject { [text] = 1 } : JsonValue.Create(text);
            }
            else input = JsonNode.Parse(vector["input"]!.GetValue<string>(), documentOptions: new JsonDocumentOptions { MaxDepth = 256 })!;
            if (refusal is not null) { Reject(() => CanonicalJson.Serialize(input), refusal, "Host refuses the shared vector: " + name); continue; }
            string digest = CanonicalJson.Sha256(input);
            Check(CanonicalJson.Serialize(input) == vector["canonical"]!.GetValue<string>() && digest == vector["sha256"]!.GetValue<string>() &&
                ComponentQualificationWindowStore.NativeHash(input) == digest, "Host canonical JSON matches the shared vector: " + name);
        }

        // Values built in code take the same path as parsed text.
        var typed = new JsonObject { ["d"] = 1e-7, ["w"] = 1000.0, ["l"] = 9007199254740993L, ["t"] = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), ["m"] = 1.10m };
        Check(CanonicalJson.Serialize(typed) == "{\"d\":1e-7,\"l\":9007199254740993,\"m\":1.1,\"t\":\"2026-11-01T00:00:00+00:00\",\"w\":1000}", "Typed values canonicalize like their JSON text");
        Reject(() => CanonicalJson.Serialize(JsonNode.Parse("1e400")!), "InvalidRequest", "A number beyond the double range is refused");
        Reject(() => CanonicalJson.Serialize(JsonNode.Parse("\"\\ud800\"")!), "InvalidRequest", "An escaped lone surrogate is refused");

        // Shortest round trip, checked against .NET's own shortest digits (exact since .NET Core 3.0).
        var samples = new List<double> { double.Epsilon, double.MaxValue, 0.1, 1e21, 1e-7, 123e-20 };
        var random = new Random(8785);
        for (int i = 0; i < 4000; i++) { double v = BitConverter.Int64BitsToDouble(random.NextInt64()); if (double.IsFinite(v)) samples.Add(v); }
        for (int i = 0; i < 4000; i++) samples.Add((float)(random.NextDouble() * Math.Pow(10, random.Next(-12, 12)))); // REAL values widened to double
        for (int exponent = -1074; exponent <= 1023; exponent += 7) samples.Add(Math.ScaleB(1, exponent)); // uneven gaps at powers of two
        for (int exponent = -323; exponent <= 308; exponent += 3)
        { double power = double.Parse("1e" + exponent, CultureInfo.InvariantCulture); samples.AddRange([power, Math.BitIncrement(power), Math.BitDecrement(power)]); }
        static string Significant(string text)
        {
            string mantissa = text.Split('e', 'E')[0].Replace("-", "").Replace(".", "");
            return mantissa.Trim('0');
        }
        foreach (double value in samples)
        {
            string text = CanonicalJsonText.Number(value)!;
            if (double.Parse(text, CultureInfo.InvariantCulture) != value || Significant(text) != Significant(value.ToString("R", CultureInfo.InvariantCulture)) || text.Contains('E'))
                throw new Exception("Number text is not the shortest round trip: " + value.ToString("R", CultureInfo.InvariantCulture) + " -> " + text);
            passed++;
        }

        // The exact literal reader both runtimes use agrees with .NET's correctly rounded double.Parse, including long
        // mantissas near halfway points and subnormals, which .NET Framework's parser misrounds.
        for (int i = 0; i < 3000; i++)
        {
            string literal = (random.Next(2) == 0 ? "-" : "") + random.Next(1, 10) + "." + string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => random.Next(10))) + "e" + random.Next(-330, 310);
            double expected = double.Parse(literal, CultureInfo.InvariantCulture);
            double? parsed = CanonicalJsonText.ParseNumber(literal);
            if (double.IsInfinity(expected) ? parsed is not null : BitConverter.DoubleToInt64Bits(parsed!.Value) != BitConverter.DoubleToInt64Bits(expected))
                throw new Exception("Exact literal reading differs from .NET: " + literal);
            passed++;
        }
        foreach (double value in samples)
            Check(CanonicalJsonText.ParseNumber(CanonicalJsonText.Number(value)!) == value && CanonicalJsonText.ParseNumber(value.ToString("R", CultureInfo.InvariantCulture)) == value, "Number text reads back exactly");
        Check(CanonicalJsonText.ParseNumber("1e400") is null && CanonicalJsonText.ParseNumber("2.4703282292062327e-324") == 0 &&
            CanonicalJsonText.ParseNumber("2.2250738585072012e-308") == double.Parse("2.2250738585072014e-308", CultureInfo.InvariantCulture), "Range and rounding edges");

        // Diffs keep exact number text even though hashes compare numbers by value.
        Check(!CanonicalJson.ExactlyEqual(JsonNode.Parse("[1]")!, JsonNode.Parse("[1.0]")!) && CanonicalJson.ExactlyEqual(JsonNode.Parse("{\"a\":1}")!, JsonNode.Parse("{ \"a\" : 1 }")!),
            "A changed observation of 1 to 1.0 is still a change");

        // Hashes stored before RFC 8785 still verify; new hashes differ only where the old form escaped or kept number text.
        var stored = new JsonObject { ["recordedAt"] = "2026-10-31T12:00:00+00:00", ["path"] = "D:\\Anlage\\Süd", ["n"] = 1 };
        string legacy = CanonicalJson.LegacySerialize(stored);
        Check(legacy == "{\"n\":1,\"path\":\"D:\\\\Anlage\\\\S\\u00FCd\",\"recordedAt\":\"2026-10-31T12:00:00\\u002B00:00\"}", "Legacy form is the pre-RFC 8785 escaping");
        string legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(legacy)));
        Check(CanonicalJson.LegacySha256(stored) == legacyHash && CanonicalJson.Sha256(stored) != legacyHash, "A '+' or non-ASCII text changes the hash");
        Check(CanonicalJson.Matches(legacyHash, stored) && CanonicalJson.Matches(CanonicalJson.Sha256(stored), stored), "Stored hashes verify in either form");
        Check(!CanonicalJson.Matches(legacyHash, new JsonObject { ["n"] = 2 }) && !CanonicalJson.Matches(null, stored), "Other content never matches");
        int Verify() => CanonicalJson.Sha256(stored) == legacyHash ? 1 : throw new BridgeException("ArtifactChanged", "Synthetic retained report differs.");
        Check(CanonicalJson.Compatible(Verify) == 1 && CanonicalJson.Sha256(stored) != legacyHash, "Retained state re-derives in its earlier form, then the current form resumes");
        Reject(() => CanonicalJson.Current(Verify, "prepare again"), CanonicalJson.FormChanged, "A pending review in the earlier form is refused with its own code");
        Reject(() => CanonicalJson.Compatible<int>(() => throw new BridgeException("ArtifactChanged", "Changed in both forms.")), "ArtifactChanged", "Changed content keeps its own refusal");
        // A transient failure on content both forms write alike is never relabelled, even when the retry passes.
        var plain = JsonNode.Parse("{\"a\":\"plain\",\"n\":[1,2]}")!; string plainHash = CanonicalJson.Sha256(plain); int calls = 0;
        int Flaky() => ++calls == 1 ? throw new BridgeException("StoreBusy", "Transient.") : CanonicalJson.Sha256(plain) == plainHash ? 1 : 0;
        Reject(() => CanonicalJson.Current(Flaky, "prepare again"), "StoreBusy", "A transient failure of a current-form review keeps its code");
        calls = 0; Reject(() => CanonicalJson.Compatible(Flaky), "StoreBusy", "Compatible does not mask a transient failure as an earlier form");
        var ascii = JsonNode.Parse("{\"b\":[1,true,null],\"a\":\"plain\"}")!;
        Check(CanonicalJson.Sha256(ascii) == CanonicalJson.LegacySha256(ascii), "Plain ASCII content keeps its hash");
        Console.WriteLine($"PASS RFC 8785 canonical JSON: {passed} checks, {vectors["cases"]!.AsArray().Count} shared vectors; no native execution.");
    }
}
