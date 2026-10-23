#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TiaMcp.Model;

namespace TiaMcp.Analysis
{
    // Pure interpretation of exact names supplied by the native V21 enums.
    // Source text, symbol spelling and instance declarations never establish a call.
    internal static class OfflineEvidence
    {
        internal static bool IsCallAccess(string access) => access == "Call" || access == "UC" || access == "CC";
        internal static bool IsCallRelation(string relation) => relation == "Uses" || relation == "UsedBy";
        internal static bool IsReadAccess(string access) => access == "Read" || access == "RW" || access == "ReadAndSymbol" || access == "ReadWriteAndSymbol";
        internal static bool IsWriteAccess(string access) => access == "Write" || access == "RW" || access == "WriteAndSymbol" || access == "ReadWriteAndSymbol";
        internal static int DependencyDirection(string relation) => relation == "Uses" || relation == "InstanceType" ? 1 : relation == "UsedBy" || relation == "TypeInstance" ? -1 : 0;

        internal static string NativeMemberKey(string ownerPath, string device, string path, string name, string address, string typeName) =>
            "native:" + string.Concat(new[] { ownerPath, device, path, name, address, typeName }
                .Select(value => value == null ? "-1:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));

        internal static bool NativeSymbolMatches(string selector, string key, string name, string address, string nativePath, string ownerPath)
        {
            if (string.IsNullOrEmpty(selector)) return false;
            if (string.Equals(selector, key, StringComparison.Ordinal) || string.Equals(selector, name, StringComparison.Ordinal) ||
                string.Equals(selector, address, StringComparison.OrdinalIgnoreCase) || string.Equals(selector, nativePath, StringComparison.Ordinal) ||
                string.Equals(selector, nativePath + "\\" + name, StringComparison.Ordinal)) return true;
            // A canonical owner prefix disambiguates identical instance/member names
            // across namespaces and units. The owner must come from native identity.
            if (!string.IsNullOrEmpty(ownerPath) && selector.StartsWith(ownerPath + "::", StringComparison.Ordinal))
            {
                selector = selector.Substring(ownerPath.Length + 2);
                if (string.Equals(selector, name, StringComparison.Ordinal)) return true;
            }
            string query = SymbolComponentsKey(selector), candidate = SymbolComponentsKey(name);
            return query != null && candidate != null && query == candidate;
        }

        // Normalize only explicit symbolic components and literal array indices.
        // Dots inside quoted names remain one component, # remains a local scope,
        // and runtime/indirect selectors have no normalized equivalent.
        private static string SymbolComponentsKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            int position = 0;
            Action skip = () => { while (position < value.Length && char.IsWhiteSpace(value[position])) position++; };
            skip();
            string result = position < value.Length && value[position] == '#' ? "local:" : "global:";
            if (result == "local:") position++;
            while (position < value.Length)
            {
                skip();
                int start = position;
                string component;
                if (position < value.Length && value[position] == '"')
                {
                    start = ++position;
                    while (position < value.Length && value[position] != '"' && value[position] != '$') position++;
                    if (position == value.Length || value[position] != '"' || position == start) return null;
                    component = value.Substring(start, position++ - start);
                }
                else
                {
                    if (position >= value.Length || !(char.IsLetter(value[position]) || value[position] == '_')) return null;
                    while (position < value.Length && (char.IsLetterOrDigit(value[position]) || value[position] == '_')) position++;
                    component = value.Substring(start, position - start);
                }
                result += "name:" + component.Length.ToString(CultureInfo.InvariantCulture) + ":" + component;
                skip();
                while (position < value.Length && value[position] == '[')
                {
                    start = ++position;
                    while (position < value.Length && value[position] != ']') position++;
                    if (position == value.Length) return null;
                    long[] indices = ParseArrayIndices(value.Substring(start, position++ - start));
                    if (indices == null) return null;
                    result += "indices:" + string.Join(",", indices.Select(index => index.ToString(CultureInfo.InvariantCulture))) + ";";
                    skip();
                }
                if (position == value.Length) return result;
                if (value[position++] != '.' || position == value.Length) return null;
            }
            return null;
        }

        internal static InstanceTargetResolution ResolveInstanceTarget(string plcPath, string instancePath, string instanceNamespace,
            string instanceOfName, bool indexTruncated, IEnumerable<InstanceTarget> indexedFbs, IEnumerable<string> nativeTypePaths)
        {
            var candidates = indexedFbs.ToArray();
            var declared = nativeTypePaths.Where(path => path != null).Distinct(StringComparer.Ordinal).ToArray();
            // A native instance/type edge identifies the actual FB independently of
            // names or namespaces. Incomplete reference enumeration cannot invalidate
            // a known native identity, but conflicting declared targets remain a gap.
            if (declared.Length == 1 && candidates.Any(candidate => candidate.Path == declared[0]))
                return new InstanceTargetResolution { Path = declared[0], NativeRelation = true,
                    Reason = "Native InstanceType/TypeInstance relation between exact DB and FB engineering identities." };
            if (declared.Length != 0)
                return new InstanceTargetResolution { Reason = "Native instance/type targets are conflicting or absent from the block identity index." };
            if (indexTruncated)
                return new InstanceTargetResolution { Reason = "Block identity index is truncated; declared FB name uniqueness is not established." };
            if (!string.IsNullOrEmpty(instanceNamespace))
                return new InstanceTargetResolution { Reason = "Namespaced instance DB has no exact native FB type relation; InstanceOfName alone does not establish the father's namespace." };
            string scope = ObjectIdentity.ScopePath(plcPath, instancePath);
            if (scope == null)
                return new InstanceTargetResolution { Reason = "The instance DB has no canonical software scope." };
            var named = candidates.Where(candidate => string.Equals(candidate.Name, instanceOfName, StringComparison.Ordinal)).Take(2).ToArray();
            if (named.Length != 1)
                return new InstanceTargetResolution { Reason = "Declared FB short name is absent or ambiguous across the complete PLC block identity index." };
            if (!string.IsNullOrEmpty(named[0].Namespace))
                return new InstanceTargetResolution { Reason = "The matching FB is namespaced; InstanceOfName alone does not establish a qualified FB identity." };
            if (!string.Equals(ObjectIdentity.ScopePath(plcPath, named[0].Path), scope, StringComparison.Ordinal))
                return new InstanceTargetResolution { Reason = "The declared FB name belongs to another software scope; no exact native type relation establishes the target." };
            return new InstanceTargetResolution { Path = named[0].Path, NativeRelation = false,
                Reason = "Unnamespaced InstanceDB.InstanceOfName, uniquely resolved in a complete PLC block index and the same canonical software scope." };
        }

        internal sealed class InstanceTarget { internal string Path, Name, Namespace; }
        internal sealed class InstanceTargetResolution { internal string Path, Reason; internal bool NativeRelation; }

        internal static IoAddress ParseIoAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value.Trim(), @"^%?(?<area>[IEQA])(?<width>[BWD]?)(?<byte>\d+)(?:\.(?<bit>[0-7]))?(?::P)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            long offset;
            if (!long.TryParse(match.Groups["byte"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > int.MaxValue) return null;
            string width = match.Groups["width"].Value.ToUpperInvariant();
            bool hasBit = match.Groups["bit"].Success;
            if (hasBit && width.Length != 0) return null;
            int bits = hasBit ? 1 : width == "W" ? 16 : width == "D" ? 32 : 8;
            return new IoAddress { Original = value, Direction = "IE".Contains(match.Groups["area"].Value.ToUpperInvariant()) ? "Input" : "Output",
                StartBit = offset * 8 + (hasBit ? int.Parse(match.Groups["bit"].Value, CultureInfo.InvariantCulture) : 0), WidthBits = bits };
        }

        internal sealed class IoAddress { internal string Original, Direction; internal long StartBit; internal int WidthBits; }

        internal static bool TryIoByteAddress(string value, out int address) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out address) && address >= 0;

        // Native Address.Length is in bits, while StartAddress is a byte offset.
        // Observed V21 DI16 fixture: adjacent module starts 0 and 2, Length=16.
        internal static int IoBitLengthToByteLength(int lengthBits) => lengthBits <= 0 ? 0 : (int)(((long)lengthBits + 7) / 8);

        internal static bool IoAddressOverlapsNativeRange(IoAddress address, int startByte, int lengthBits)
        {
            if (address == null || !ValidIoByteRange(startByte, IoBitLengthToByteLength(lengthBits))) return false;
            long startBit = (long)startByte * 8;
            return address.StartBit < startBit + lengthBits && startBit < address.StartBit + address.WidthBits;
        }

        internal sealed class IoRangeSnapshot { internal string IoType; internal int StartAddress, LengthBits; }

        internal static bool IoAddressMutationPreserved(IEnumerable<IoRangeSnapshot> before, IEnumerable<IoRangeSnapshot> after,
            string ioType, int oldStart, int requestedStart)
        {
            var original = before.ToArray();
            if (original.Count(row => row.IoType == ioType && row.StartAddress == oldStart) != 1) return false;
            Func<IoRangeSnapshot, int, string> key = (row, start) => row.IoType + ":" + start.ToString(CultureInfo.InvariantCulture) + ":" + row.LengthBits.ToString(CultureInfo.InvariantCulture);
            var expected = original.Select(row => key(row, row.IoType == ioType && row.StartAddress == oldStart ? requestedStart : row.StartAddress)).OrderBy(value => value, StringComparer.Ordinal);
            var actual = after.Select(row => key(row, row.StartAddress)).OrderBy(value => value, StringComparer.Ordinal);
            return expected.SequenceEqual(actual, StringComparer.Ordinal);
        }

        internal static bool ValidIoByteRange(int start, int length) => start >= 0 && length > 0 && (long)start + length <= (long)int.MaxValue + 1;
        internal static bool IoByteRangesOverlap(int firstStart, int firstLength, int secondStart, int secondLength) =>
            (long)firstStart < (long)secondStart + secondLength && (long)secondStart < (long)firstStart + firstLength;
        internal static string IoOwnershipResolution(IEnumerable<bool?> controllers)
        {
            var owners = controllers.ToArray();
            if (owners.Any(owner => owner == true)) return "verified";
            return owners.Length == 0 || owners.Any(owner => !owner.HasValue) ? "unresolved" : "otherPlc";
        }
        internal static bool IoOwnershipMayMatch(IEnumerable<bool?> controllers) => IoOwnershipResolution(controllers) != "otherPlc";

        // Read-only normalization. Native expressions are retained as text and are
        // never evaluated. Sparse Subelement overrides are not a complete array.
        internal static InterfaceData NormalizeInterface(XDocument document, Func<bool> take, Action truncate)
        {
            var result = new InterfaceData();
            if (document.Root == null) return result;
            foreach (var owner in document.Root.Elements().Where(e => e.Name.LocalName.StartsWith("SW.", StringComparison.Ordinal)))
            {
                if (!take()) break;
                var attributes = owner.Elements().FirstOrDefault(e => e.Name.LocalName == "AttributeList");
                var declaration = new ObjectDeclaration { Kind = owner.Name.LocalName, Name = ChildValue(attributes, "Name"),
                    Namespace = ChildValue(attributes, "Namespace"), InstanceOfName = ChildValue(attributes, "InstanceOfName"),
                    InstanceOfNumber = ChildValue(attributes, "InstanceOfNumber"), XmlLine = XmlLine(owner) };
                result.Declarations.Add(declaration);
                var nativeInterface = attributes?.Elements().FirstOrDefault(e => e.Name.LocalName == "Interface");
                if (nativeInterface == null) continue;
                foreach (var section in nativeInterface.Elements().Where(e => e.Name.LocalName == "Sections").Elements().Where(e => e.Name.LocalName == "Section"))
                {
                    if (!take()) break;
                    var normalized = new InterfaceSection { Name = section.Attribute("Name")?.Value, OwnerName = declaration.Name,
                        OwnerKind = declaration.Kind, XmlLine = XmlLine(section) };
                    ReadMembers(section, normalized.Members, new string[0], normalized.Name, 0, take, truncate);
                    result.Sections.Add(normalized);
                }
            }
            return result;
        }

        private static void ReadMembers(XElement parent, List<InterfaceMember> output, string[] ancestors, string section,
            int depth, Func<bool> take, Action truncate)
        {
            if (depth > 64) { truncate(); return; }
            foreach (var member in parent.Elements().Where(e => e.Name.LocalName == "Member"))
            {
                if (!take()) return;
                var row = new InterfaceMember { Name = member.Attribute("Name")?.Value, DataType = member.Attribute("Datatype")?.Value,
                    Section = section, XmlLine = XmlLine(member) };
                row.Path = ancestors.Concat(new[] { row.Name }).ToArray();
                foreach (var attribute in member.Attributes()) row.XmlAttributes.Add(attribute.Name.ToString(), attribute.Value);
                foreach (string field in new[] { "StartValue", "DefaultValue", "Comment", "AttributeList" })
                {
                    var element = member.Elements().FirstOrDefault(e => e.Name.LocalName == field);
                    if (element != null) row.FieldXml.Add(field, element.ToString(SaveOptions.DisableFormatting));
                }
                row.StartValue = ChildValue(member, "StartValue");
                row.DefaultValue = ChildValue(member, "DefaultValue");
                row.Array = ParseArrayDeclaration(row.DataType);
                var sections = member.Elements().Where(e => e.Name.LocalName == "Sections").Elements().Where(e => e.Name.LocalName == "Section").ToArray();
                row.DeclarationKind = row.Array != null ? "array" : string.Equals(row.DataType, "Struct", StringComparison.OrdinalIgnoreCase) ? "structure" :
                    sections.Any(s => new[] { "Input", "Output", "InOut" }.Contains(s.Attribute("Name")?.Value)) ? "multiInstance" :
                    IsQuotedType(row.DataType) ? "namedType" : "value";
                row.DeclaredType = IsQuotedType(row.DataType) ? row.DataType.Substring(1, row.DataType.Length - 2) : null;
                ReadMembers(member, row.Members, row.Path, section, depth + 1, take, truncate);
                foreach (var nested in sections)
                {
                    if (!take()) break;
                    // Retain empty sections, too: they carry declaration evidence.
                    row.Sections.Add(nested.Attribute("Name")?.Value);
                    ReadMembers(nested, row.Members, row.Path, nested.Attribute("Name")?.Value, depth + 1, take, truncate);
                }
                foreach (var subelement in member.Elements().Where(e => e.Name.LocalName == "Subelement"))
                {
                    if (!take()) break;
                    string path = subelement.Attribute("Path")?.Value;
                    var value = new ArrayValue { NativePath = path, StartValue = ChildValue(subelement, "StartValue"),
                        DefaultValue = ChildValue(subelement, "DefaultValue"), Xml = subelement.ToString(SaveOptions.DisableFormatting), XmlLine = XmlLine(subelement) };
                    value.Indices = ParseArrayIndices(path);
                    if (value.Indices == null) value.UnresolvedReason = "Native subelement path is not a plain integer index tuple; retained without interpretation.";
                    else if (row.Array == null || !row.Array.BoundsResolved) value.UnresolvedReason = "Array bounds are not resolved literal integers.";
                    else if (value.Indices.Length != row.Array.Dimensions.Count) value.UnresolvedReason = "Index rank differs from the declared array rank.";
                    else if (value.Indices.Where((index, i) => index < row.Array.Dimensions[i].Lower || index > row.Array.Dimensions[i].Upper).Any())
                        value.UnresolvedReason = "Index is outside the declared array bounds.";
                    row.ElementValues.Add(value);
                }
                output.Add(row);
            }
        }

        internal static ArrayDeclaration ParseArrayDeclaration(string dataType) => ParseArrayDeclaration(dataType, 0);

        private static ArrayDeclaration ParseArrayDeclaration(string dataType, int depth)
        {
            if (dataType == null || depth > 64) return null;
            var match = Regex.Match(dataType, @"^\s*Array\s*\[(?<bounds>[^\]]+)\]\s+of\s+(?<element>.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return null;
            var result = new ArrayDeclaration { ElementType = match.Groups["element"].Value, BoundsResolved = true };
            result.ElementArray = ParseArrayDeclaration(result.ElementType, depth + 1);
            foreach (string dimension in match.Groups["bounds"].Value.Split(','))
            {
                var bounds = Regex.Match(dimension, @"^\s*(?<lower>[+-]?\d+)\s*\.\.\s*(?<upper>[+-]?\d+)\s*$", RegexOptions.CultureInvariant);
                long lower, upper;
                bool valid = bounds.Success && long.TryParse(bounds.Groups["lower"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out lower)
                    && long.TryParse(bounds.Groups["upper"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out upper) && lower <= upper;
                // Do not compute products or expand potentially enormous dimensions.
                result.Dimensions.Add(new ArrayDimension { Declaration = dimension.Trim(),
                    Lower = valid ? (long?)long.Parse(bounds.Groups["lower"].Value, CultureInfo.InvariantCulture) : null,
                    Upper = valid ? (long?)long.Parse(bounds.Groups["upper"].Value, CultureInfo.InvariantCulture) : null });
                result.BoundsResolved &= valid;
            }
            return result;
        }

        private static long[] ParseArrayIndices(string path)
        {
            if (path == null || !Regex.IsMatch(path, @"^\s*[+-]?\d+(?:\s*,\s*[+-]?\d+)*\s*$", RegexOptions.CultureInvariant)) return null;
            var values = new List<long>();
            foreach (string part in path.Split(','))
            {
                long index;
                if (!long.TryParse(part.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out index)) return null;
                values.Add(index);
            }
            return values.ToArray();
        }

        private static bool IsQuotedType(string value) => value != null && value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"';
        private static string ChildValue(XElement parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
        private static int XmlLine(XElement element) => ((IXmlLineInfo)element).LineNumber;

        internal sealed class InterfaceData
        {
            internal readonly List<InterfaceSection> Sections = new List<InterfaceSection>();
            internal readonly List<ObjectDeclaration> Declarations = new List<ObjectDeclaration>();
        }
        internal sealed class ObjectDeclaration { internal string Kind, Name, Namespace, InstanceOfName, InstanceOfNumber; internal int XmlLine; }
        internal sealed class InterfaceSection
        {
            internal string Name, OwnerName, OwnerKind; internal int XmlLine;
            internal readonly List<InterfaceMember> Members = new List<InterfaceMember>();
        }
        internal sealed class InterfaceMember
        {
            internal string Name, DataType, Section, DeclarationKind, DeclaredType, StartValue, DefaultValue;
            internal int XmlLine; internal string[] Path; internal ArrayDeclaration Array;
            internal readonly Dictionary<string, string> XmlAttributes = new Dictionary<string, string>();
            internal readonly Dictionary<string, string> FieldXml = new Dictionary<string, string>();
            internal readonly List<string> Sections = new List<string>();
            internal readonly List<InterfaceMember> Members = new List<InterfaceMember>();
            internal readonly List<ArrayValue> ElementValues = new List<ArrayValue>();
        }
        internal sealed class ArrayDeclaration
        {
            internal string ElementType; internal bool BoundsResolved; internal ArrayDeclaration ElementArray;
            internal readonly List<ArrayDimension> Dimensions = new List<ArrayDimension>();
        }
        internal sealed class ArrayDimension { internal string Declaration; internal long? Lower, Upper; }
        internal sealed class ArrayValue { internal string NativePath, StartValue, DefaultValue, Xml, UnresolvedReason; internal long[] Indices; internal int XmlLine; }
    }
}
