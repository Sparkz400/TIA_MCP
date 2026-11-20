#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // Pure guards of the safety and drive change kinds, kept apart from OpennessSession so they run offline against fakes.

    /// <summary>Hardware edits (delete, move, replace, address set) never touch an item that is, or sits above or below, F-I/O or a PROFIsafe drive telegram.</summary>
    internal static class SafetyHardwareRules
    {
        /// <summary>The label of the first node on the ancestor chain or in the subtree (the node itself included) that is safety hardware; null when none is.</summary>
        internal static string FirstSafety<T>(T item, Func<T, IEnumerable<T>> children, Func<T, T> parent, Func<T, bool> isSafety, Func<T, string> label) where T : class
        {
            int depth = 0;
            for (var up = item; up != null; up = parent(up))
            {
                if (++depth > 64) throw new BridgeException("TraversalLimit", "Cannot inspect the complete hardware chain for safety items.");
                if (isSafety(up)) return label(up);
            }
            return Below(item, children, isSafety, label, 0);
        }

        private static string Below<T>(T item, Func<T, IEnumerable<T>> children, Func<T, bool> isSafety, Func<T, string> label, int depth) where T : class
        {
            if (depth > 64) throw new BridgeException("TraversalLimit", "Cannot inspect the complete hardware subtree for safety items.");
            foreach (var child in children(item))
            {
                if (isSafety(child)) return label(child);
                var found = Below(child, children, isSafety, label, depth + 1);
                if (found != null) return found;
            }
            return null;
        }

        internal static void Require(string safetyLabel, string action)
        {
            if (safetyLabel != null)
                throw new BridgeException("SafetyBoundary", "The hardware item or its container or subitems include " + safetyLabel +
                    ", which is safety hardware or whose F-I/O or PROFIsafe status cannot be read; " + action +
                    " is refused because it could change F-I/O or PROFIsafe addressing outside the safety workflows. Nothing was written.");
        }
    }

    /// <summary>Drive parameter write limits shared by drive_parameter_set and drive_motor_configure.</summary>
    internal static class DriveWriteRules
    {
        // Safety Integrated (p9300..p10999) and commissioning, reset, save and motor identification parameters trigger drive
        // functions; telegram selection (p922, p60022) bypasses drive_telegram_set's SafetyTelegram refusal and address checks;
        // p15 and p340 recalculate or reset whole parameter groups; p802..p804 select the motor-data-set switch-over; p8920..p8925
        // are the PROFINET identity (names, addresses). All need dedicated workflows, not a scalar write.
        private static readonly int[] ProtectedNumbers = { 9, 10, 15, 340, 802, 803, 804, 922, 970, 971, 972, 976, 977, 1900, 1910, 1960, 3900, 60022 };

        internal static bool Protected(int number) =>
            number >= 9300 && number <= 10999 || number >= 8920 && number <= 8925 || Array.IndexOf(ProtectedNumbers, number) >= 0;

        /// <summary>Refuses a value provably outside the parameter's declared limits. A limit that is null, unreadable or not numeric proves nothing and admits.</summary>
        internal static void RequireWithinLimits(string name, object value, object min, object max)
        {
            if (!TryNumber(value, out double number)) return;
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new BridgeException("InvalidRequest", "The value for " + name + " is not a finite number; nothing was written.");
            if (TryNumber(min, out double low) && number < low)
                throw new BridgeException("InvalidRequest", "The value " + Text(value) + " for " + name + " is below the declared minimum " + Text(min) + "; nothing was written.");
            if (TryNumber(max, out double high) && number > high)
                throw new BridgeException("InvalidRequest", "The value " + Text(value) + " for " + name + " is above the declared maximum " + Text(max) + "; nothing was written.");
        }

        private static string Text(object value) => value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : value?.ToString();

        // Booleans, text and enums are not ranges.
        private static bool TryNumber(object value, out double number)
        {
            switch (value)
            {
                case sbyte _: case byte _: case short _: case ushort _: case int _: case uint _: case long _: case ulong _: case float _: case double _: case decimal _:
                    number = Convert.ToDouble(value, CultureInfo.InvariantCulture); return true;
                default: number = 0; return false;
            }
        }

        /// <summary>The parameters changed beyond the written ones: name, before and after, bounded; added and removed names are listed as such.</summary>
        internal static JObject SideEffects(IReadOnlyDictionary<string, JToken> before, IReadOnlyDictionary<string, JToken> after, ISet<string> written, bool complete, int cap = 200)
        {
            var changed = new JArray(); int total = 0;
            foreach (var name in before.Keys.Union(after.Keys).OrderBy(key => key, StringComparer.Ordinal))
            {
                if (written.Contains(name)) continue;
                before.TryGetValue(name, out var x); after.TryGetValue(name, out var y);
                if (JToken.DeepEquals(x, y)) continue;
                if (++total <= cap) changed.Add(new JObject { ["name"] = name, ["before"] = x, ["after"] = y,
                    ["change"] = x == null ? "added" : y == null ? "removed" : "changed" });
            }
            return new JObject { ["complete"] = complete, ["changedCount"] = total, ["changedListed"] = changed.Count, ["changed"] = changed,
                ["basis"] = "Every parameter value of the drive object read before and after the write, except the written parameters, which the readback checks one by one." };
        }

        /// <summary>The first parameter (by name) per number among the wanted numbers, for limit lookups that must not scan the drive once per row.</summary>
        internal static Dictionary<int, T> FirstPerNumber<T>(IEnumerable<T> parameters, Func<T, int> number, ISet<int> wanted)
        {
            var result = new Dictionary<int, T>();
            foreach (var parameter in parameters)
            {
                int n = number(parameter);
                if (wanted.Contains(n) && !result.ContainsKey(n)) result.Add(n, parameter);
                if (result.Count == wanted.Count) break;
            }
            return result;
        }
    }

    /// <summary>A batch of native writes that does not stay half-applied: a failure restores the earlier writes (best effort) and says what could not be restored.</summary>
    internal static class CompensatingWrites
    {
        internal static void Apply<T>(IReadOnlyList<T> items, Action<T> write, Action<T> restore, Func<T, string> label)
        {
            for (int i = 0; i < items.Count; i++)
            {
                try { write(items[i]); }
                catch (Exception error)
                {
                    var restored = new JArray(); var failed = new JArray();
                    // The failing setter itself is restored too: a native setter may apply before it throws.
                    for (int back = i; back >= 0; back--)
                    {
                        try { restore(items[back]); restored.Add(label(items[back])); }
                        catch (Exception) { failed.Add(label(items[back])); }
                    }
                    throw new BridgeException("MutationVerificationFailed", "The write of " + label(items[i]) + " failed (" + error.Message + "); " + restored.Count +
                        " write(s) were restored to their previous values and " + failed.Count + " could not be restored (" + string.Join(", ", failed) + "). Restore the checkpoint before further changes.");
                }
            }
        }
    }

    /// <summary>F-destination address reuse across the F-I/O of a safety PLC's F-source and of the rest of the project.</summary>
    internal static class FDestinationRules
    {
        /// <summary>Paths whose current address equals the value, or that another write of the batch sets to it. An unreadable address throws when it must be readable, else it is skipped.</summary>
        internal static List<string> Duplicates(JToken value, IEnumerable<(string Path, JToken Current, bool PlannedSame)> others, bool unreadableRefuses)
        {
            var paths = new List<string>();
            foreach (var other in others)
            {
                if (other.Current?.Type != JTokenType.Integer)
                {
                    if (unreadableRefuses) throw new BridgeException("DependencyCoverageIncomplete", "An F-destination address is unreadable; collision absence cannot be established: " + other.Path);
                    continue;
                }
                // Current and planned addresses both count, so swaps and chained moves refuse rather than pass through a transient duplicate.
                if (JToken.DeepEquals(other.Current, value) || other.PlannedSame) paths.Add(other.Path);
            }
            return paths;
        }
    }

    /// <summary>Name and address collisions of the PROFIsafe tags safety_drive_tags creates, over every software scope of the PLC.</summary>
    internal static class SafetyTagCollisionRules
    {
        /// <param name="symbolNames">Every tag, user constant and system constant name of the target scope (TIA keeps them unique there).</param>
        /// <param name="tags">Every tag of every software scope of the PLC (root, software units, safety units) with its logical address.</param>
        /// <returns>The first collision (ObjectExists for a name, AddressInUse for an address with the covering tag), or null.</returns>
        internal static (string Code, string Message, string Tag, string Address)? Find(IEnumerable<string> symbolNames, IEnumerable<(string Name, string Address)> tags, string name, string address)
        {
            if (symbolNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                return ("ObjectExists", "A tag, constant or system constant named " + name + " already exists in the PLC's tag tables.", null, null);
            foreach (var tag in tags)
                if (TagAddressRules.IoOverlaps(tag.Address, address))
                    return ("AddressInUse", "The existing tag " + tag.Name + " (" + tag.Address + ") already covers " + address + ".", tag.Name, tag.Address);
            return null;
        }
    }

    /// <summary>Prepared changes nobody applied: dropped from the worker after a TTL or above a count, oldest first. The journal on disk stays.</summary>
    internal static class PreparedChangeEviction
    {
        internal static readonly TimeSpan Ttl = TimeSpan.FromHours(12);
        internal const int MaxPrepared = 64;

        /// <summary>Ids of changes in state "prepared" that are older than the TTL, or beyond the newest <paramref name="max"/>. Any other state is never evicted.</summary>
        internal static List<string> Select(IEnumerable<(string Id, string State, DateTime PreparedUtc)> changes, DateTime now, TimeSpan ttl, int max)
        {
            var prepared = changes.Where(change => change.State == "prepared").OrderByDescending(change => change.PreparedUtc).ThenBy(change => change.Id, StringComparer.Ordinal).ToList();
            return prepared.Where((change, index) => index >= max || now - change.PreparedUtc > ttl).Select(change => change.Id).ToList();
        }
    }
}
