using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Model
{
    /// <summary>Pure fail-closed validation shared by the HMI adapters and offline tests.</summary>
    internal static class HmiRecipeRules
    {
        internal static void RequireFamily(string family)
        {
            if (family != "classic" && family != "unified")
                throw new ArgumentException("HMI family must be exactly classic or unified.");
        }

        internal static void RequireSameFamily(string sourceFamily, string targetFamily)
        {
            RequireFamily(sourceFamily); RequireFamily(targetFamily);
            if (sourceFamily != targetFamily) throw new ArgumentException("Classic and Unified components are not interchangeable.");
        }

        internal static void RequireName(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
                || value.IndexOfAny(new[] { '/', '\\' }) >= 0 || value == "." || value == "..")
                throw new ArgumentException(field + " must be an explicit native name, without path separators or control characters (1–256 characters).");
        }

        internal static void RequirePlacement(long left, long top, long width, long height, long screenWidth, long screenHeight)
        {
            if (screenWidth < 1 || screenHeight < 1) throw new ArgumentException("Native screen dimensions are unavailable.");
            if (left < 0 || top < 0 || width < 1 || height < 1 || left > int.MaxValue || top > int.MaxValue
                || width > uint.MaxValue || height > uint.MaxValue || left + width > screenWidth || top + height > screenHeight)
                throw new ArgumentException("The requested faceplate rectangle must fit wholly within the selected screen.");
        }

        internal static void RequireBindings(IEnumerable<string> requested, IEnumerable<string> available, IEnumerable<string> required)
        {
            var requestedList = (requested ?? throw new ArgumentNullException(nameof(requested))).ToList();
            var availableSet = new HashSet<string>(available ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var requestedSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in requestedList)
            {
                RequireName(name, "interface property");
                if (!requestedSet.Add(name)) throw new ArgumentException("Duplicate interface binding: " + name);
                if (!availableSet.Contains(name)) throw new ArgumentException("Unknown interface property: " + name);
            }
            foreach (var name in required ?? Enumerable.Empty<string>())
                if (!requestedSet.Contains(name)) throw new ArgumentException("Required interface property is unbound: " + name);
        }

        internal static void RequireExactVersion(Guid requested, Guid observed)
        {
            if (requested == Guid.Empty || observed == Guid.Empty || requested != observed)
                throw new ArgumentException("Native library version readback does not match the exact requested version GUID.");
        }

        internal static void RequireTypeCompatibility(string expected, string actual, int? expectedLength = null, int? actualLength = null)
        {
            if (string.IsNullOrWhiteSpace(expected) || !string.Equals(expected, actual, StringComparison.Ordinal))
                throw new ArgumentException("Binding requires an exact native data type match; implicit PLC/HMI conversions are not inferred.");
            if (expectedLength != actualLength) throw new ArgumentException("Binding string/array capacity does not match.");
        }

        internal static string ExactContainedType(string typeName, Version version)
        {
            RequireName(typeName, "typeName");
            if (version == null || version.Build < 0) throw new ArgumentException("The native type must have a concrete three- or four-part version.");
            return "V" + version + "\\" + typeName;
        }

        internal static void RequireNewInternalTagDefault(string defaultConnection, string observedConnection, string plcTag, bool resolvesNativeConnection)
        {
            // Native internal-tag labels are localized. Preserve the actual default rather than translating it.
            if (!string.Equals(defaultConnection, observedConnection, StringComparison.Ordinal)
                || !string.IsNullOrEmpty(plcTag) || resolvesNativeConnection)
                throw new ArgumentException("A newly-created internal tag unexpectedly changed connection or acquired a PLC binding.");
        }
    }
}
