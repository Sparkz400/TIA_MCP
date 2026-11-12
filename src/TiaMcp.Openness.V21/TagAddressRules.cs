#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    // A PLC tag on an F-I/O address becomes a safety tag in TIA. tag_create, tag_set, tag_table_import_xml and project-ward VCI
    // imports of a tag table decide this before writing anything by comparing each requested absolute address with the F-I/O
    // address ranges of the selected PLC; library_instantiate of a tag-table master copy, whose addresses it cannot read, by any range.
    internal static class TagAddressRules
    {
        // International (I/Q) and German (E/A) mnemonics; bit, byte, word, double word and long word accesses.
        private static readonly Regex IoAddress = new Regex(@"^%?(?<area>[IQEA])(?:(?<byte>\d{1,6})\.(?<bit>[0-7])|(?<size>[BWDL])(?<start>\d{1,6}))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Conservative lexical scan: comments and quoted text are included because source generation can create tags.
        private static readonly Regex SourceIo = new Regex(@"(?<![\w])%?P?[IQEA]\s*(?:\d{1,6}\s*\.\s*[0-7]|[BWDL]\s*\d{1,6})(?![\w.])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static string[] SourceIoAddresses(string source) => SourceIo.Matches(source).Cast<Match>()
            .Select(match => Regex.Replace(Regex.Replace(match.Value, @"\s+", ""), @"^%?[Pp]", "%")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        internal static string[] BlockIoAddresses(XDocument document)
        {
            var result = new List<string>(SourceIoAddresses(document.ToString(SaveOptions.DisableFormatting)));
            foreach (var address in document.Descendants().Where(e => e.Name.LocalName == "Address"))
            {
                string area = address.Attribute("Area")?.Value;
                if (area == "PeripheryInput") area = "Input";
                if (area == "PeripheryOutput") area = "Output";
                if (area != "Input" && area != "Output") continue;
                if (!int.TryParse(address.Attribute("BitOffset")?.Value, out int offset) || offset < 0 || offset > 7999999)
                    throw new BridgeException("SafetyBoundary", "An absolute I/O operand has an unqualified bit offset on a PLC with F-I/O.");
                int width = address.Attribute("Type")?.Value switch
                {
                    "Bool" => 1, "Byte" or "Char" or "SInt" or "USInt" => 8,
                    "Word" or "Int" or "UInt" => 16, "DWord" or "DInt" or "UDInt" or "Real" => 32,
                    "LWord" or "LInt" or "ULInt" or "LReal" => 64, _ => 0
                };
                if (width == 0) throw new BridgeException("SafetyBoundary", "An absolute I/O operand has an unqualified type on a PLC with F-I/O.");
                string prefix = area == "Input" ? "%I" : "%Q";
                if (width == 1) result.Add(prefix + (offset / 8) + "." + (offset % 8));
                else for (int bit = offset; bit < offset + width; bit++) result.Add(prefix + (bit / 8) + "." + (bit % 8));
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>The process-image direction and bit range of an absolute input or output address, or null for anything else
        /// (bit memory, data blocks, symbolic or unparseable text).</summary>
        internal static (string IoType, int StartBit, int LengthBits)? Io(string logicalAddress)
        {
            var match = IoAddress.Match(logicalAddress?.Trim() ?? "");
            if (!match.Success) return null;
            char area = char.ToUpperInvariant(match.Groups["area"].Value[0]);
            string ioType = area == 'I' || area == 'E' ? "Input" : "Output";
            if (match.Groups["byte"].Success) return (ioType, int.Parse(match.Groups["byte"].Value) * 8 + int.Parse(match.Groups["bit"].Value), 1);
            int bytes = "BWDL".IndexOf(char.ToUpperInvariant(match.Groups["size"].Value[0])) switch { 0 => 1, 1 => 2, 2 => 4, _ => 8 };
            return (ioType, int.Parse(match.Groups["start"].Value) * 8, bytes * 8);
        }

        /// <summary>Every PLC tag's LogicalAddress in a SimaticML tag-table document (user constants have none).</summary>
        internal static string[] TagTableAddresses(XDocument document) =>
            document.Descendants("SW.Tags.PlcTag").Select(tag => tag.Element("AttributeList")?.Element("LogicalAddress")?.Value).Where(address => address != null).ToArray();

        /// <summary>The first F-I/O with a non-empty range, or null. V21 exposes only a master copy's content names, so the addresses a
        /// tag-table master copy brings are unknown before library_instantiate creates it; any such range refuses it.</summary>
        internal static string AnyFIo(IEnumerable<(string IoType, int StartByte, int LengthBits, string Path)> fIoRanges) =>
            fIoRanges.FirstOrDefault(range => range.LengthBits > 0).Path;

        /// <summary>The bit address of bit <paramref name="bit"/> (0..15) of the 16-bit word at %IW/%QW <paramref name="wordStart"/>.
        /// S7 words are big-endian: byte <c>wordStart</c> holds bits 8..15 and byte <c>wordStart + 1</c> bits 0..7, so word bit 0 is
        /// %I(wordStart+1).0 and word bit 8 is %I(wordStart).0.</summary>
        internal static string WordBitAddress(bool input, int wordStart, int bit)
        {
            if (bit < 0 || bit > 15) throw new ArgumentOutOfRangeException(nameof(bit), "A word bit is 0..15.");
            return (input ? "%I" : "%Q") + (wordStart + (bit < 8 ? 1 : 0)) + "." + (bit % 8);
        }

        /// <summary>True when two absolute I/O addresses address at least one common bit in the same direction.</summary>
        internal static bool IoOverlaps(string left, string right)
        {
            var a = Io(left);
            var b = Io(right);
            return a != null && b != null && a.Value.IoType == b.Value.IoType &&
                a.Value.StartBit < b.Value.StartBit + b.Value.LengthBits && b.Value.StartBit < a.Value.StartBit + a.Value.LengthBits;
        }

        /// <summary>The first F-I/O range (ioType, start byte, length in bits, path) the address overlaps, or null.</summary>
        internal static string FIoOverlap(string logicalAddress, IEnumerable<(string IoType, int StartByte, int LengthBits, string Path)> fIoRanges)
        {
            var io = Io(logicalAddress);
            if (io == null) return null;
            var (ioType, start, length) = io.Value;
            return fIoRanges.FirstOrDefault(range => string.Equals(range.IoType, ioType, StringComparison.Ordinal) && range.LengthBits > 0 &&
                start < range.StartByte * 8 + range.LengthBits && range.StartByte * 8 < start + length).Path;
        }
    }
}
