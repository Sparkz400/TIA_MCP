using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using TiaMcp.Core;
using TiaMcp.Core.Engineering;
using TiaMcp.Translations;

/// <summary>
/// Seeded mutations of the retained synthetic workbook through TranslationWorkbook.Open, which reads the package with
/// BoundedOpcPackage: raw bytes, ZIP directory fields, and repacked parts with valid CRCs. The package is opened or
/// refused with a BridgeException; no other exception, and every case returns.
/// </summary>
internal static class PackageRobustnessChecks
{
    internal static string Run(IReadOnlyDictionary<string, byte[]> parts, WorkbookProfile profile, EngineeringObjectReference identity)
    {
        string[] names = parts.Keys.Order(StringComparer.Ordinal).ToArray();
        byte[] original = Pack(names.Select(name => (name, parts[name])), CompressionLevel.Optimal);
        void Open(byte[] bytes)
        {
            var workbook = TranslationWorkbook.Open(bytes, profile, identity, Hash(bytes));
            _ = TranslationReview.Review(workbook, workbook, []);
        }
        Open(original);
        string[] oddNames = ["../evil.xml", "xl/../../evil.xml", "xl\\evil.xml", "/absolute.xml", "xl/", "xl//double.xml", "xl/./dot.xml", "C:evil.xml", "xl/%2e%2e/x.xml", "xl/a#b.xml",
            "xl/a?b.xml", "xl/äöü.xml", "xl/" + new string('n', 300) + ".xml", "xl/worksheets/sheet2.xml", "xl/vbaProject.bin", "[Content_Types].XML", "_rels/.RELS", "xl/nul\u0001.xml"];

        (byte[], string) Mutate(Random random)
        {
            int roll = random.Next(100);
            if (roll < 25) return SeededMutations.Bytes(original, random);
            if (roll < 50) return Directory(original, random);
            var changed = names.Select(name => (Name: name, Bytes: parts[name])).ToList();
            var kinds = new List<string>();
            for (int n = random.Next(1, 3); n > 0; n--)
            {
                int index = random.Next(changed.Count);
                switch (random.Next(8))
                {
                    case 0: case 1: case 2: case 3:
                    {
                        var xml = changed.Select((part, at) => (part, at)).Where(p => p.part.Name.EndsWith(".xml", StringComparison.Ordinal) || p.part.Name.EndsWith(".rels", StringComparison.Ordinal)).ToArray();
                        var (part, at) = xml[random.Next(xml.Length)];
                        var (bytes, kind) = SeededMutations.Xml(part.Bytes, random);
                        changed[at] = (part.Name, bytes); kinds.Add(part.Name + ": " + kind); break;
                    }
                    case 4: kinds.Add("part removed: " + changed[index].Name); changed.RemoveAt(index); break;
                    case 5: { string name = oddNames[random.Next(oddNames.Length)]; changed.Add((name, changed[index].Bytes)); kinds.Add("part added: " + name); break; }
                    case 6: changed.Add(changed[index]); kinds.Add("part duplicated: " + changed[index].Name); break;
                    default:
                    {
                        int size = random.Next(2) == 0 ? 70_000 : 3_000_000;
                        changed.Add((random.Next(2) == 0 ? "xl/big.xml" : "xl/media/big.bin", new byte[size])); kinds.Add("large part (" + size + ")"); break;
                    }
                }
            }
            return (Pack(changed, random.Next(3) == 0 ? CompressionLevel.NoCompression : CompressionLevel.Optimal), string.Join(" + ", kinds));
        }

        // One fixed case the seeded run reaches only by chance: a part name that is not UTF-8 while both headers claim it is.
        byte[] badName = original.ToArray();
        int end = badName.Length - 22, central = (int)BinaryPrimitives.ReadUInt32LittleEndian(badName.AsSpan(end + 16)), local = (int)BinaryPrimitives.ReadUInt32LittleEndian(badName.AsSpan(central + 42));
        badName[central + 46] = 0xFF; badName[local + 30] = 0xFF;
        foreach (int flags in new[] { central + 8, local + 6 }) badName[flags + 1] |= 0x08;
        try { Open(badName); throw new Exception("A part name that is not UTF-8 was accepted."); }
        catch (BridgeException) { }

        var report = SeededMutations.Run("OPC package", 20261125, 1500, Mutate, Open, error => (error as BridgeException)?.Code, TimeSpan.FromSeconds(10), KnownFailures);
        return report.Summary("ZIP/OPC package robustness");
    }

    /// <summary>Known product defects in BoundedOpcPackage; empty when the package refuses every mutation with its own code.</summary>
    private static readonly SeededMutations.KnownFailure[] KnownFailures = [];

    private static byte[] Pack(IEnumerable<(string Name, byte[] Bytes)> entries, CompressionLevel level)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name, level); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open(); stream.Write(bytes);
            }
        return output.ToArray();
    }

    /// <summary>Overwrites one field of the end record, a central directory entry or its local header.</summary>
    private static (byte[], string) Directory(byte[] source, Random random)
    {
        byte[] copy = source.ToArray();
        int end = copy.Length - 22;
        while (end >= 0 && BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(end)) != 0x06054b50) end--;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(end + 10)), cursor = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(end + 16));
        var entries = new List<int>();
        for (int i = 0; i < count; i++)
        {
            entries.Add(cursor);
            cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(cursor + 28)) + BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(cursor + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(cursor + 32));
        }
        int entry = entries[random.Next(entries.Count)], local = (int)BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(entry + 42));
        int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(entry + 28));
        uint[] wide = [0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFE, 0xFFFFFFFF, (uint)copy.Length, (uint)random.Next()];
        ushort[] narrow = [0, 1, 8, 9, 12, 14, 99, 0x0800, 0x0801, 0x0808, 0x0001, 0x0008, 0x7FFF, 0xFFFF];
        void U32(int at, string name, out string kind) { uint value = wide[random.Next(wide.Length)]; BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(at), value); kind = name + "=" + value; }
        void U16(int at, string name, out string kind) { ushort value = narrow[random.Next(narrow.Length)]; BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(at), value); kind = name + "=" + value; }
        string kind;
        switch (random.Next(22))
        {
            case 0: U16(entry + 8, "central flags", out kind); break;
            case 1: U16(entry + 10, "central method", out kind); break;
            case 2: U32(entry + 16, "central crc", out kind); break;
            case 3: U32(entry + 20, "central compressed size", out kind); break;
            case 4: U32(entry + 24, "central size", out kind); break;
            case 5: U16(entry + 28, "central name length", out kind); break;
            case 6: U16(entry + 30, "central extra length", out kind); break;
            case 7: U16(entry + 32, "central comment length", out kind); break;
            case 8: U16(entry + 34, "central disk", out kind); break;
            case 9: U32(entry + 42, "central local offset", out kind); break;
            case 10: U16(local + 6, "local flags", out kind); break;
            case 11: U16(local + 8, "local method", out kind); break;
            case 12: U32(local + 14, "local crc", out kind); break;
            case 13: U32(local + 18, "local compressed size", out kind); break;
            case 14: U32(local + 22, "local size", out kind); break;
            case 15: U16(local + 26, "local name length", out kind); break;
            case 16: U16(local + 28, "local extra length", out kind); break;
            case 17: U16(end + 10, "end entry count", out kind); BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(end + 8), BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(end + 10))); break;
            case 18: U32(end + 12, "end directory size", out kind); break;
            case 19: U32(end + 16, "end directory offset", out kind); break;
            case 20:
            {
                // The same bytes in both headers, so only the name itself is wrong; with and without the UTF-8 flag.
                byte value = (byte)(random.Next(3) switch { 0 => 0xFF, 1 => 0xC0, _ => 0x00 }); int at = random.Next(nameLength);
                copy[entry + 46 + at] = value; copy[local + 30 + at] = value;
                bool utf8 = random.Next(2) == 0;
                foreach (int flags in new[] { entry + 8, local + 6 })
                    BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(flags), (ushort)(utf8 ? BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(flags)) | 0x800 : BinaryPrimitives.ReadUInt16LittleEndian(copy.AsSpan(flags)) & ~0x800));
                kind = $"name byte {value:X2}, UTF-8 flag {utf8}"; break;
            }
            default:
            {
                // Both headers agree on a method or flag the reader may not support.
                ushort method = narrow[random.Next(narrow.Length)]; bool flags = random.Next(2) == 0;
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(entry + (flags ? 8 : 10)), method); BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(local + (flags ? 6 : 8)), method);
                kind = (flags ? "both flags=" : "both methods=") + method; break;
            }
        }
        return (copy, "directory: " + kind);
    }
}
