using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using TiaMcp.Core;
using TiaMcp.Core.Diagnostics;

/// <summary>
/// Seeded mutations of retained block exports through the review store's SimaticML reader (ProjectReviewStore.XmlSignatures)
/// and of an explicit-mapping CSV through the trace import (TraceCsvReader). A mutated input is read or refused; it never
/// raises an exception the reader does not own, and never fails to return.
/// </summary>
internal static class ParserRobustnessChecks
{
    private const int Seed = 20261125;

    internal static void Run()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "fixtures", "trace-diagnostics", "FB_Line.xml"))) directory = directory.Parent;
        string fixtures = Path.Combine(directory?.FullName ?? throw new Exception("fixtures/trace-diagnostics is missing"), "fixtures");
        byte[][] blocks = new[] { "trace-diagnostics/FB_Line.xml", "trace-diagnostics/DB_Line.xml", "trace-diagnostics/FB_Conveyor.xml", "plcsim-process-image/PipCyclic.xml" }
            .Select(name => File.ReadAllBytes(Path.Combine(fixtures, name))).ToArray();
        foreach (byte[] block in blocks) ProjectReviewStore.XmlSignatures(Encoding.UTF8.GetString(block));

        // The block reader takes text the worker exported; its own refusal is XmlException (the 128-level bound throws one
        // itself), which every Host caller turns into a coded result (ToolResult.IsRefusal).
        var xml = SeededMutations.Run("SimaticML block XML", Seed, 4000,
            random => SeededMutations.Xml(blocks[random.Next(blocks.Length)], random),
            input => ProjectReviewStore.XmlSignatures(new UTF8Encoding(false, false).GetString(input)),
            error => error is BridgeException bridge ? bridge.Code : error is XmlException ? "XmlException" : null,
            TimeSpan.FromSeconds(10), minimumOutcomes: 2);
        Console.WriteLine("PASS " + xml.Summary("SimaticML block XML robustness"));

        // No CSV is retained under fixtures/; this is the shape TiaMcp.Diagnostics.Tests imports (CsvMapping).
        var plan = new JsonObject { ["reviewId"] = "review-1", ["signals"] = new JsonArray(
            new JsonObject { ["id"] = "run", ["datatype"] = "Bool" }, new JsonObject { ["id"] = "speed", ["datatype"] = "Real" }, new JsonObject { ["id"] = "count", ["datatype"] = "Int" }) };
        JsonObject Options() => new()
        {
            ["delimiter"] = ";", ["decimalSeparator"] = ".", ["headerRecord"] = 0, ["timeColumn"] = "seconds", ["timeUnit"] = "s", ["mode"] = "synthetic",
            ["clock"] = "synthetic", ["programIdentity"] = "fixture", ["columns"] = new JsonArray(
                new JsonObject { ["signalId"] = "run", ["column"] = "run", ["unit"] = "bool" }, new JsonObject { ["signalId"] = "speed", ["column"] = "speed", ["unit"] = "rpm" },
                new JsonObject { ["signalId"] = "count", ["column"] = "count", ["unit"] = "1" })
        };
        var artifact = new JsonObject { ["sha256"] = new string('a', 64) };
        var lines = new List<string> { "seconds;run;speed;count" };
        for (int i = 0; i < 40; i++) lines.Add(FormattableString.Invariant($"{(i - 2) / 100m:0.00};{i % 2};{(i * 12.5m):0.0};{i}"));
        lines.Add("0.39;1;\"1500.5\";\"\"");
        byte[] csv = Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
        if (TraceCsvReader.Convert(csv, Options(), plan, "trace-1", artifact)["samples"]!.AsArray().Count != 41) throw new Exception("The CSV baseline no longer imports.");

        string[] tokens = ["", "\"", "\"\"", ";", ";;;", ",", "\t", "\r", "\n", "\r\n", "1e400", "-", "NaN", "Infinity", "0x1F", "1,5", "1.5.5", "79228162514264337593543950336",
            "-79228162514264337593543950336", "0.0000000000000000000000000000001", "TRUE", "2", " 1", "1 ", "﻿", "\u0000", "seconds", new string('9', 4097), new string(';', 300)];
        (byte[], string) MutateCsv(Random random)
        {
            int roll = random.Next(10);
            if (roll < 3) return SeededMutations.Bytes(csv, random);
            var rows = Encoding.UTF8.GetString(csv).Split("\r\n").ToList();
            string kind;
            switch (roll)
            {
                case 3: rows.Insert(random.Next(rows.Count), rows[random.Next(rows.Count)]); kind = "record duplicated"; break;
                case 4: rows.RemoveAt(random.Next(rows.Count)); kind = "record removed"; break;
                case 5:
                {
                    int row = random.Next(rows.Count); var fields = rows[row].Split(';').ToList();
                    fields[random.Next(fields.Count)] = tokens[random.Next(tokens.Length)]; rows[row] = string.Join(';', fields); kind = "field replaced"; break;
                }
                case 6:
                {
                    int row = random.Next(rows.Count);
                    rows[row] = rows[row].Insert(random.Next(rows[row].Length + 1), tokens[random.Next(tokens.Length)]); kind = "token inserted"; break;
                }
                case 7:
                {
                    int count = random.Next(3) switch { 0 => 300, 1 => 10_200, _ => 60_000 };
                    rows.InsertRange(1, Enumerable.Repeat(rows[1 + random.Next(rows.Count - 2)], count)); kind = "many records (" + count + ")"; break;
                }
                case 8: rows = rows.Select(r => r.Replace(';', random.Next(2) == 0 ? ',' : '\t')).ToList(); kind = "other delimiter"; break;
                default: rows[random.Next(rows.Count)] += string.Concat(Enumerable.Repeat(";1", random.Next(1, 400))); kind = "extra fields"; break;
            }
            return (Encoding.UTF8.GetBytes(string.Join(random.Next(8) == 0 ? "\n" : "\r\n", rows)), kind);
        }
        var text = SeededMutations.Run("trace CSV", Seed, 5000, MutateCsv, input => TraceCsvReader.Convert(input, Options(), plan, "trace-1", artifact),
            error => (error as BridgeException)?.Code, TimeSpan.FromSeconds(10));
        Console.WriteLine("PASS " + text.Summary("Trace CSV import robustness"));

        // The mapping is caller-supplied JSON. A wrong shape is refused with a code, or raises the JSON node's own
        // InvalidOperationException, which the diagnostic tools turn into DiagnosticFailed (DiagnosticTools.DiagnosticResult).
        // Anything else would leave the Host as a protocol error.
        int shapes = 0, uncoded = 0;
        var untyped = new List<string>();
        void Shape(string name, Action<JsonObject> change, string? knownFailure = null)
        {
            var options = Options(); change(options);
            try { TraceCsvReader.Convert(csv, options, plan, "trace-1", artifact); shapes++; }
            catch (BridgeException) { shapes++; }
            catch (InvalidOperationException error) { uncoded++; untyped.Add(name + ": uncoded InvalidOperationException: " + error.Message); }
            catch (Exception error)
            {
                if (knownFailure == error.GetType().Name) { Console.WriteLine($"KNOWN-DEFECT Trace CSV options: {name} raises {knownFailure} instead of a refusal (TraceCsvReader.Convert)."); shapes++; }
                else untyped.Add(name + ": " + error.GetType().Name + ": " + error.Message);
            }
        }
        Shape("headerRecord as a string", o => o["headerRecord"] = "0");
        Shape("headerRecord as a fraction", o => o["headerRecord"] = 0.5);
        Shape("headerRecord as an object", o => o["headerRecord"] = new JsonObject());
        Shape("headerRecord beyond Int32", o => o["headerRecord"] = 4294967296L);
        Shape("headerRecord negative", o => o["headerRecord"] = -1);
        Shape("headerRecord absent", o => o.Remove("headerRecord"));
        Shape("missingValues holding null", o => o["missingValues"] = new JsonArray((JsonNode?)null), KnownMissingValueNull);
        Shape("missingValues holding a number", o => o["missingValues"] = new JsonArray(1));
        Shape("missingValues holding an object", o => o["missingValues"] = new JsonArray(new JsonObject()));
        Shape("missingValues as a string", o => o["missingValues"] = "x");
        Shape("missingValues with 17 tokens", o => o["missingValues"] = new JsonArray(Enumerable.Range(0, 17).Select(i => (JsonNode)("m" + i)).ToArray()));
        Shape("columns as an object", o => o["columns"] = new JsonObject());
        Shape("columns holding null", o => o["columns"] = new JsonArray(null, null, null));
        Shape("columns holding a string", o => o["columns"] = new JsonArray("run", "speed", "count"));
        Shape("columns holding numbers as names", o => o["columns"]![0]!["column"] = 1);
        Shape("columns with a duplicate signal", o => o["columns"]![1]!["signalId"] = "run");
        Shape("columns with the time column", o => o["columns"]![0]!["column"] = "seconds");
        Shape("delimiter as a number", o => o["delimiter"] = 1);
        Shape("delimiter of two characters", o => o["delimiter"] = ";;");
        Shape("decimalSeparator as null", o => o["decimalSeparator"] = null);
        Shape("timeColumn as an array", o => o["timeColumn"] = new JsonArray());
        Shape("quality as a string", o => o["quality"] = "good");
        Shape("an oversized option", o => o["clock"] = new string('c', 70_000));
        if (untyped.Count != 0) throw new Exception("Trace CSV options raise untyped exceptions:" + Environment.NewLine + string.Join(Environment.NewLine, untyped));
        Console.WriteLine($"PASS Trace CSV option shapes: {shapes} checks; {uncoded} wrong-typed values reach the caller uncoded; every one is now a coded refusal.");
    }

    // Fixed on 25 November: missingValues = [null] used to dereference the null entry; it is now refused as InvalidRequest.
    private const string? KnownMissingValueNull = null;
}
