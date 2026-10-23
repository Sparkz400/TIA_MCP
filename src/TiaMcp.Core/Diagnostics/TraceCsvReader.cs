using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace TiaMcp.Core.Diagnostics;

/// <summary>Explicit column mapping for exported text; never infers a Siemens binary/file schema.</summary>
internal static class TraceCsvReader
{
    internal static JsonObject Convert(byte[] bytes, JsonObject options, JsonObject plan, string tracePlanId, JsonObject original)
    {
        D.Bound(options);
        string delimiter = D.Required(options, "delimiter", 1), separator = D.Required(options, "decimalSeparator", 1);
        if (delimiter is not ("," or ";" or "\t") || separator is not ("." or ",")) throw new BridgeException("InvalidRequest", "Specify comma/semicolon/tab delimiter and dot/comma decimalSeparator.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new BridgeException("MeasurementFormatUnsupported", "CSV must be UTF-8; convert another encoding explicitly and preserve the original."); }
        var records = Parse(text, delimiter[0]);
        int headerIndex = options["headerRecord"]?.GetValue<int>() ?? -1;
        if (headerIndex < 0 || headerIndex > 100 || headerIndex >= records.Count - 1) throw new BridgeException("InvalidRequest", "Explicit headerRecord (zero-based 0..100) must precede data records.");
        var header = records[headerIndex];
        if (header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.Ordinal).Count() != header.Length) throw new BridgeException("MeasurementFormatUnsupported", "Header names must be nonempty and unique; no positional inference.");
        int Column(string name) { int index = Array.IndexOf(header, name); return index >= 0 ? index : throw new BridgeException("MeasurementLineageMismatch", "CSV column not found: " + name); }
        int timeColumn = Column(D.Required(options, "timeColumn", 512));
        var signals = D.Rows(plan["signals"]).OfType<JsonObject>().ToDictionary(s => D.Text(s, "id"), StringComparer.Ordinal);
        var mappings = options["columns"] as JsonArray ?? throw new BridgeException("InvalidRequest", "Explicit signal columns are required.");
        if (!mappings.Select(c => D.Text(c, "signalId")).ToHashSet(StringComparer.Ordinal).SetEquals(signals.Keys) || mappings.Count != signals.Count)
            throw new BridgeException("MeasurementLineageMismatch", "CSV mappings must cover each selected signal exactly once.");
        var columns = mappings.Select(c => (Id: D.Required(c!, "signalId", 64), Index: Column(D.Required(c!, "column", 512)), Unit: D.Required(c!, "unit", 64))).ToArray();
        if (columns.Select(c => c.Index).Distinct().Count() != columns.Length || columns.Any(c => c.Index == timeColumn)) throw new BridgeException("InvalidRequest", "CSV signal/time columns must be distinct.");
        var missing = options["missingValues"] is JsonArray missingValues ? missingValues.Select(n => n!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal) { "" };
        if (missing.Count > 16) throw new BridgeException("InvalidRequest", "At most 16 explicit missing-value tokens.");
        var samples = new JsonArray(); var units = new JsonObject(); foreach (var c in columns) units[c.Id] = c.Unit;
        for (int row = headerIndex + 1; row < records.Count; row++)
        {
            var fields = records[row];
            if (fields.Length != header.Length) throw new BridgeException("MeasurementIncomplete", "CSV record width differs from header at record " + row + ". No trailing records are silently discarded.");
            var values = new JsonObject();
            foreach (var c in columns)
            {
                string value = fields[c.Index];
                if (missing.Contains(value)) { values[c.Id] = null; continue; }
                if (D.Text(signals[c.Id], "datatype").Equals("Bool", StringComparison.OrdinalIgnoreCase))
                    values[c.Id] = value switch { "1" or "true" or "TRUE" => true, "0" or "false" or "FALSE" => false,
                        _ => throw new BridgeException("MeasurementFormatUnsupported", "Bool CSV value must be 0/1 or true/false, not an inferred numeric conversion.") };
                else values[c.Id] = Number(value, separator);
            }
            samples.Add(new JsonObject { ["time"] = Number(fields[timeColumn], separator), ["values"] = values });
            if (samples.Count > 10000) throw new BridgeException("ArtifactCapacity", "CSV import exceeds 10000 samples; split explicitly without losing lineage.");
        }
        return new JsonObject { ["schemaVersion"] = "trace-measurement.v1", ["tracePlanId"] = tracePlanId, ["reviewId"] = plan["reviewId"]!.DeepClone(),
            ["mode"] = D.Required(options, "mode", 40), ["timeUnit"] = D.Required(options, "timeUnit", 16), ["clock"] = D.Required(options, "clock", 512),
            ["programIdentity"] = D.Required(options, "programIdentity", 2000), ["quality"] = options["quality"]?.DeepClone(),
            ["settingsReadbackEvidence"] = options["settingsReadbackEvidence"]?.DeepClone(), ["units"] = units, ["samples"] = samples,
            ["originalArtifact"] = original.DeepClone(), ["conversion"] = "Explicit UTF-8 CSV column mapping; delimiters/decimal mark/units/clock supplied by caller. No vendor dialect or native quality qualification.",
            ["csvMapping"] = options.DeepClone() };
    }
    private static decimal Number(string value, string separator)
    {
        if (separator == ",") { if (value.Contains('.')) throw new BridgeException("MeasurementFormatUnsupported", "Unexpected decimal point in comma-decimal CSV."); value = value.Replace(',', '.'); }
        return TraceNumber.Parse(value);
    }
    private static List<string[]> Parse(string text, char delimiter)
    {
        var records = new List<string[]>(); var fields = new List<string>(); var field = new StringBuilder(); bool quoted = false, closed = false, started = false;
        void Field() { fields.Add(field.ToString()); field.Clear(); closed = false; started = false; if (fields.Count > 256) throw new BridgeException("ArtifactCapacity", "CSV exceeds 256 columns."); }
        void Record() { Field(); records.Add(fields.ToArray()); fields.Clear(); if (records.Count > 10102) throw new BridgeException("ArtifactCapacity", "CSV record budget exceeded."); }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; } else { quoted = false; closed = true; } }
                else field.Append(c);
            }
            else if (c == delimiter) Field();
            else if (c is '\r' or '\n') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; Record(); }
            else if (c == '"' && !started && !closed) { quoted = true; started = true; }
            else { if (closed || c == '"') throw new BridgeException("MeasurementFormatUnsupported", "Malformed CSV quoting."); field.Append(c); started = true; }
            if (field.Length > 4096) throw new BridgeException("ArtifactCapacity", "CSV field exceeds 4096 characters.");
        }
        if (quoted) throw new BridgeException("MeasurementIncomplete", "Unterminated CSV quote.");
        if (field.Length > 0 || fields.Count > 0 || started || closed) Record();
        return records;
    }
}
