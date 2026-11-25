// Seeded, deterministic input mutation for the document parsers. Linked explicitly (<Compile Include>) by the test
// projects that own a parser; every case derives its own Random from (seed, case), so one case replays alone.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

internal static class SeededMutations
{
    /// <summary>An exception that is neither the parser's typed refusal nor a listed known failure.</summary>
    internal sealed record Finding(int Case, string Mutation, string Signature, string Detail, byte[] Input);

    /// <summary>A known product defect the suite tolerates: exception type and a method on its stack. Reported, never silent.</summary>
    internal sealed record KnownFailure(string Name, string ExceptionType, string StackContains);

    internal sealed class Report
    {
        internal int Cases, Accepted, Refused;
        internal SortedDictionary<string, int> Refusals { get; } = new(StringComparer.Ordinal);
        internal SortedDictionary<string, int> Known { get; } = new(StringComparer.Ordinal);
        internal List<Finding> Findings { get; } = [];
        internal TimeSpan Slowest;
        internal string Summary(string suite) =>
            $"{suite}: {Cases} seeded cases, {Accepted} accepted, {Refused} typed refusals ({string.Join(", ", Refusals.Select(p => p.Key + "=" + p.Value))}); slowest case {Slowest.TotalMilliseconds:F0} ms" +
            (Known.Count == 0 ? "" : "; KNOWN untyped failures: " + string.Join(", ", Known.Select(p => p.Key + "=" + p.Value)));
    }

    internal static Random CaseRandom(int seed, int index) => new(unchecked(seed * 1000003 + index * 7919 + 17));

    /// <summary>
    /// Runs <paramref name="cases"/> mutations. <paramref name="refusal"/> returns the code of a typed refusal or null for
    /// any other exception. Throws when a case throws an unlisted untyped exception, when one case outlasts
    /// <paramref name="perCase"/> (the process is ended by the watchdog if the parser never returns), or when the mutations
    /// only ever reach one outcome (a vacuous suite).
    /// </summary>
    internal static Report Run(string suite, int seed, int cases, Func<Random, (byte[] Input, string Mutation)> mutate, Action<byte[]> parse,
        Func<Exception, string?> refusal, TimeSpan perCase, IReadOnlyList<KnownFailure>? known = null, int minimumOutcomes = 3)
    {
        var report = new Report { Cases = cases };
        int current = -1; string mutation = ""; long startedTicks = 0;
        using var watchdog = new Timer(_ =>
        {
            long started = Interlocked.Read(ref startedTicks);
            if (started == 0 || Stopwatch.GetElapsedTime(started) < perCase + perCase) return;
            Console.Error.WriteLine($"FAIL {suite}: case {Volatile.Read(ref current)} ({mutation}, seed {seed}) did not return within {(perCase + perCase).TotalSeconds:F0} s; ending the process.");
            Console.Error.Flush();
            Environment.Exit(3);
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < cases; index++)
        {
            var (input, kind) = mutate(CaseRandom(seed, index));
            Volatile.Write(ref current, index); mutation = kind;
            if (kind.Contains("deepest", StringComparison.Ordinal))
            { Console.WriteLine($"NOTE {suite}: case {index} is {kind}; a stack overflow after this line belongs to it."); Console.Out.Flush(); }
            long started = Stopwatch.GetTimestamp(); Interlocked.Exchange(ref startedTicks, started);
            try { parse(input); report.Accepted++; }
            catch (Exception error)
            {
                string? code = refusal(error);
                if (code is not null) { report.Refused++; report.Refusals[code] = report.Refusals.GetValueOrDefault(code) + 1; }
                else
                {
                    string stack = error.StackTrace ?? "";
                    var listed = known?.FirstOrDefault(k => k.ExceptionType == error.GetType().Name && stack.Contains(k.StackContains, StringComparison.Ordinal));
                    if (listed is not null) report.Known[listed.Name] = report.Known.GetValueOrDefault(listed.Name) + 1;
                    else
                    {
                        string site = stack.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("TiaMcp", StringComparison.Ordinal)) ?? stack.Split('\n')[0].Trim();
                        string signature = error.GetType().Name + " @ " + site;
                        if (signatures.Add(signature)) report.Findings.Add(new(index, kind, signature, error.Message, input));
                    }
                }
            }
            Interlocked.Exchange(ref startedTicks, 0);
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > report.Slowest) report.Slowest = elapsed;
            if (elapsed > perCase) throw new Exception($"{suite}: case {index} ({kind}, seed {seed}) took {elapsed.TotalSeconds:F1} s, over the {perCase.TotalSeconds:F0} s bound.");
        }
        if (report.Findings.Count != 0)
        {
            string directory = Path.Combine(Path.GetTempPath(), "tiamcp-fuzz-" + suite.Replace(' ', '-') + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            foreach (var finding in report.Findings) File.WriteAllBytes(Path.Combine(directory, "case-" + finding.Case + ".bin"), finding.Input);
            throw new Exception($"{suite}: {report.Findings.Count} untyped exception(s) with seed {seed}; inputs kept in {directory}:" + Environment.NewLine +
                string.Join(Environment.NewLine, report.Findings.Select(f => $"  case {f.Case} [{f.Mutation}] {f.Signature}: {f.Detail}")));
        }
        if ((report.Accepted > 0 ? 1 : 0) + report.Refusals.Count < minimumOutcomes)
            throw new Exception($"{suite}: mutations reached fewer than {minimumOutcomes} distinct outcomes; the suite proves nothing. " + report.Summary(suite));
        return report;
    }

    // ---- Byte-level mutations: for any format. ----

    internal static (byte[] Input, string Mutation) Bytes(byte[] source, Random random)
    {
        byte[] copy = source.ToArray();
        switch (random.Next(6))
        {
            case 0:
                for (int n = random.Next(1, 4); n > 0; n--) copy[random.Next(copy.Length)] ^= (byte)(1 << random.Next(8));
                return (copy, "bit flips");
            case 1:
                for (int n = random.Next(1, 9); n > 0; n--) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                return (copy, "byte overwrites");
            case 2:
                return (copy[..random.Next(copy.Length)], "truncation");
            case 3:
            {
                int at = random.Next(copy.Length), length = Math.Min(random.Next(1, 4096), copy.Length - at);
                return ([.. copy[..at], .. copy[(at + length)..]], "range removal");
            }
            case 4:
            {
                int at = random.Next(copy.Length), length = Math.Min(random.Next(1, 4096), copy.Length - at);
                return ([.. copy[..(at + length)], .. copy[at..]], "range duplication");
            }
            default:
            {
                int at = random.Next(copy.Length + 1); byte[] noise = new byte[random.Next(1, 64)]; random.NextBytes(noise);
                return ([.. copy[..at], .. noise, .. copy[at..]], "random insertion");
            }
        }
    }

    // ---- XML mutations. Most keep the document well-formed, so they reach the code behind the XML lexer. ----

    private static readonly string[] Values =
    [
        "", " ", "0", "-1", "1", "2147483647", "2147483648", "-2147483649", "4294967296", "99999999999999999999999999", "1e400", "NaN", "-0", "0x10", "1.5", "1,5",
        "true", "TRUE", "null", "ns=65536;i=1", "ns=-1;s=x", "i=99999999999", "ns=1;g=not-a-guid", "ns=1;b=!!!", "s=", "1:", ":", "2:Name", "99999:Name",
        "http://[", "urn:", "../../x", "C:\\x", "\u00e4\u00f6\u00fc\u03a9", "\ud83d\ude00", "\ufffd", "a\tb", "a\nb", "'\"<>&", "{}", "[]", "#", "%00", "2026-13-45T25:61:61Z", "P1Y"
    ];

    internal static (byte[] Input, string Mutation) Xml(byte[] source, Random random)
    {
        int roll = random.Next(100);
        if (roll < 12) return Bytes(source, random);
        if (roll < 20) return Lexical(source, random);
        XDocument document;
        try { using var stream = new MemoryStream(source, false); document = XDocument.Load(stream, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { return Bytes(source, random); } // an input an earlier mutation already broke
        var kinds = new List<string>();
        for (int n = roll < 80 ? 1 : random.Next(2, 5); n > 0; n--) kinds.Add(Structural(document, random));
        // A mutated tree the writer itself refuses (a namespace clash) is replaced by a byte mutation; still deterministic.
        try { return (Save(document), string.Join(" + ", kinds)); }
        catch (Exception error) when (error is System.Xml.XmlException or ArgumentException or InvalidOperationException) { return Bytes(source, random); }
    }

    private static byte[] Save(XDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(buffer, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), CheckCharacters = false, OmitXmlDeclaration = document.Declaration is null }))
            document.Save(writer);
        return buffer.ToArray();
    }

    private static string Structural(XDocument document, Random random)
    {
        var elements = document.Root!.DescendantsAndSelf().ToArray(); // document order: stable across runs
        var target = elements[random.Next(elements.Length)];
        var attributes = elements.SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration)).ToArray();
        switch (random.Next(14))
        {
            case 0:
                if (target == document.Root) goto case 5;
                for (int n = random.Next(1, 4); n > 0; n--) target.AddAfterSelf(new XElement(target));
                return "element duplicated";
            case 1:
                if (target == document.Root) goto case 6;
                target.Remove();
                return "element removed";
            case 2:
            {
                var other = elements[random.Next(elements.Length)];
                if (target == document.Root || other == document.Root || target.DescendantsAndSelf().Contains(other) || other.DescendantsAndSelf().Contains(target)) goto case 7;
                var copy = new XElement(other); other.ReplaceWith(new XElement(target)); target.ReplaceWith(copy);
                return "elements swapped";
            }
            case 3:
            {
                var parent = elements[random.Next(elements.Length)];
                if (target == document.Root || target.DescendantsAndSelf().Contains(parent)) goto case 8;
                var copy = new XElement(target); target.Remove(); parent.AddFirst(copy);
                return "element moved";
            }
            case 4:
            {
                int length = random.Next(4) switch { 0 => 8193, 1 => 65536, 2 => 300_000, _ => 1_100_000 };
                if (attributes.Length != 0 && random.Next(2) == 0) attributes[random.Next(attributes.Length)].Value = new string('A', length);
                else target.SetAttributeValue("fuzz" + random.Next(1000), new string('A', length));
                return "huge attribute (" + length + ")";
            }
            case 5:
                if (attributes.Length == 0) goto case 9;
                attributes[random.Next(attributes.Length)].Value = Values[random.Next(Values.Length)];
                return "attribute value replaced";
            case 6:
                if (attributes.Length == 0) goto case 9;
                attributes[random.Next(attributes.Length)].Remove();
                return "attribute removed";
            case 7:
            {
                var leaves = elements.Where(e => !e.HasElements).ToArray();
                if (leaves.Length == 0) goto case 9;
                leaves[random.Next(leaves.Length)].Value = random.Next(6) == 0 ? new string('7', random.Next(2) == 0 ? 5000 : 200_000) : Values[random.Next(Values.Length)];
                return "text replaced";
            }
            case 8:
            {
                // Wrap the target's content in nested copies of an element name the format knows, or of a foreign one.
                int depth = random.Next(5) switch { 0 => 60, 1 => 65, 2 => 129, 3 => 300, _ => 1000 };
                XName name = random.Next(2) == 0 ? target.Name : XName.Get("w", "urn:fuzz");
                var inner = new XElement(name, target.Nodes()); var outer = inner;
                for (int i = 1; i < depth; i++) outer = new XElement(name, outer);
                target.ReplaceNodes(outer);
                return "nesting " + depth;
            }
            case 9:
                target.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
                target.Name =random.Next(3) switch { 0 => XName.Get(target.Name.LocalName, "urn:fuzz"), 1 => XName.Get(target.Name.LocalName), _ => target.Name.Namespace + ("X" + target.Name.LocalName) };
                return "element renamed";
            case 10:
                target.Add(new XElement(elements[random.Next(elements.Length)].Name, Values[random.Next(Values.Length)]));
                return "foreign child added";
            case 11:
                target.AddFirst(random.Next(3) switch { 0 => new XComment("fuzz"), 1 => new XProcessingInstruction("fuzz", "x=1"), _ => new XCData("<x>&y;]]") });
                return "comment, PI or CDATA added";
            case 12:
            {
                int count = random.Next(3) switch { 0 => 200, 1 => 2000, _ => 60_000 };
                var child = new XElement(target.Name.Namespace + "n"); var many = new object[count];
                for (int i = 0; i < count; i++) many[i] = child;
                target.Add(many);
                return "many children (" + count + ")";
            }
            default:
                if (attributes.Length == 0) goto case 9;
                var attribute = attributes[random.Next(attributes.Length)];
                attribute.Parent!.SetAttributeValue(XName.Get(attribute.Name.LocalName, "urn:fuzz"), attribute.Value);
                return "attribute copied to a foreign namespace";
        }
    }

    /// <summary>Text-level changes an XML writer cannot produce: DTDs, entities, encodings and unbalanced or extreme nesting.</summary>
    private static (byte[] Input, string Mutation) Lexical(byte[] source, Random random)
    {
        string text = new UTF8Encoding(false).GetString(source).TrimStart('\uFEFF');
        int declaration = text.StartsWith("<?xml", StringComparison.Ordinal) ? text.IndexOf("?>", StringComparison.Ordinal) + 2 : 0;
        string prolog = text[..declaration], body = text[declaration..];
        int rootStart = body.IndexOf('<', StringComparison.Ordinal);
        while (rootStart >= 0 && rootStart + 1 < body.Length && body[rootStart + 1] is '!' or '?') rootStart = body.IndexOf('<', rootStart + 1);
        int rootOpenEnd = rootStart < 0 ? -1 : body.IndexOf('>', rootStart);
        var utf8 = new UTF8Encoding(false);
        switch (random.Next(11))
        {
            case 0:
            {
                var entities = new StringBuilder("<!DOCTYPE lolz [<!ENTITY a0 \"aaaaaaaaaa\">");
                for (int i = 1; i < 12; i++) entities.Append("<!ENTITY a").Append(i).Append(" \"").Append(string.Concat(Enumerable.Repeat("&a" + (i - 1) + ";", 10))).Append("\">");
                entities.Append("]>");
                return (utf8.GetBytes(prolog + entities + InsertText(body, rootOpenEnd, "&a11;")), "DTD entity expansion");
            }
            case 1:
                return (utf8.GetBytes(prolog + "<!DOCTYPE x [<!ENTITY xxe SYSTEM \"file:///C:/Windows/win.ini\">]>" + InsertText(body, rootOpenEnd, "&xxe;")), "DTD external entity");
            case 2:
                return (utf8.GetBytes(prolog + "<!DOCTYPE x SYSTEM \"http://192.0.2.1/x.dtd\">" + body), "DTD external subset");
            case 3:
                return (utf8.GetBytes(prolog + InsertText(body, rootOpenEnd, "&undeclared;")), "undeclared entity");
            case 4:
                return (Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(body)).ToArray(), "UTF-16 with BOM");
            case 5:
                return (utf8.GetBytes("<?xml version=\"1.0\" encoding=\"" + (random.Next(2) == 0 ? "utf-7" : "x-unknown") + "\"?>" + body), "odd declared encoding");
            case 6:
                return ([0xEF, 0xBB, 0xBF, .. utf8.GetBytes(prolog + body)], "UTF-8 BOM");
            case 7:
            {
                bool deepest = random.Next(4) == 0; int depth = deepest ? 200_000 : random.Next(2) == 0 ? 130 : 20_000;
                string open = string.Concat(Enumerable.Repeat("<w>", depth)), close = string.Concat(Enumerable.Repeat("</w>", depth));
                return (utf8.GetBytes(prolog + InsertText(body, rootOpenEnd, open + close)), (deepest ? "deepest " : "") + "lexical nesting " + depth);
            }
            case 8:
                return (utf8.GetBytes(prolog + InsertText(body, rootOpenEnd, string.Concat(Enumerable.Repeat("<w>", random.Next(1, 5000))))), "unclosed elements");
            case 9:
                return (utf8.GetBytes(prolog + body + body), "second root");
            default:
                return (utf8.GetBytes(prolog + InsertText(body, rootOpenEnd, "\u0001\u0000&#0;&#xD800;")), "forbidden characters");
        }
    }

    private static string InsertText(string body, int rootOpenEnd, string text) =>
        rootOpenEnd < 0 || body[rootOpenEnd - 1] == '/' ? body + text : body.Insert(rootOpenEnd + 1, text);
}
