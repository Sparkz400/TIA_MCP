#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    // A PLC tag on an F-I/O address becomes a safety tag in TIA. tag_create, tag_set and tag_table_import_xml decide this before
    // writing anything by comparing each requested absolute address with the F-I/O address ranges of the selected PLC.
    internal static class TagAddressRules
    {
        // International (I/Q) and German (E/A) mnemonics; bit, byte, word, double word and long word accesses.
        private static readonly Regex IoAddress = new Regex(@"^%?(?<area>[IQEA])(?:(?<byte>\d{1,6})\.(?<bit>[0-7])|(?<size>[BWDL])(?<start>\d{1,6}))$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
