#nullable disable
using System;

namespace TiaMcp.Openness.V21
{
    /// <summary>The save state of an open project as TIA reports it: the marker the content cache relies on.</summary>
    internal sealed class FingerprintSaveState
    {
        internal readonly int? ProcessId;
        internal readonly string ProjectPath, LastModified;
        internal readonly bool IsModified;

        internal FingerprintSaveState(int? processId, string projectPath, string lastModified, bool isModified)
        { ProcessId = processId; ProjectPath = projectPath; LastModified = lastModified; IsModified = isModified; }

        /// <summary>Only a saved project in a known TIA process can be recognized again.</summary>
        internal bool Saved => ProcessId != null && !IsModified && !string.IsNullOrEmpty(ProjectPath) && !string.IsNullOrEmpty(LastModified);

        internal bool SameSavedStateAs(FingerprintSaveState other) => Saved && other != null && other.Saved && ProcessId == other.ProcessId
            && string.Equals(ProjectPath, other.ProjectPath, StringComparison.OrdinalIgnoreCase) && string.Equals(LastModified, other.LastModified, StringComparison.Ordinal);
    }

    // A change fingerprint reads the whole project (hardware, software scopes, every block's native fingerprints) on every run:
    // at prepare, twice during shared-mutation admission, before the checkpoint and after the save. Between an applied-and-saved
    // change and the next one the project is usually untouched, so the last read is kept and reused, but only while TIA reports
    // the project as saved (IsModified false) with the same LastModified, in the same TIA process, for the same change kind and
    // target blocks. The fingerprint value is the one a full read would produce; anything else is read again.
    //
    // The cache holds one entry. It is dropped before every worker operation that is not part of the prepare/apply/status
    // sequence of a prepared (non-library) change, and by the worker itself before it saves, mutates or closes a project,
    // so the worker's own writes never rely on the marker. The marker alone covers edits made outside the worker
    // (the TIA user interface, another Openness client) between those operations.
    internal sealed class FingerprintContentCache<T> where T : class
    {
        private FingerprintSaveState _state;
        private string _variant;
        private T _content;

        internal T Find(FingerprintSaveState now, string variant) =>
            _content != null && now != null && now.SameSavedStateAs(_state) && string.Equals(variant, _variant, StringComparison.Ordinal) ? _content : null;

        /// <summary>Keeps the content only if the project was saved, and in the same saved state, both before and after it was read.</summary>
        internal bool Store(FingerprintSaveState before, FingerprintSaveState after, string variant, T content)
        {
            Clear();
            if (content == null || variant == null || before == null || !before.SameSavedStateAs(after)) return false;
            _state = after; _variant = variant; _content = content;
            return true;
        }

        internal void Clear() { _state = null; _variant = null; _content = null; }
    }

    internal static class FingerprintContentRules
    {
        /// <summary>True for the worker operations that leave the kept project content in place: the prepare, admission, apply
        /// and status steps of a non-library prepared change, and session status. Every other operation drops it first.</summary>
        /// <param name="kind">change_prepare's kind.</param>
        /// <param name="changeId">The changeId of a mutation_begin's change_apply/change_restore arguments.</param>
        /// <param name="admittedOperation">The operation of the shared mutation in progress, for mutation_dispatch and mutation_end.</param>
        /// <param name="admittedChangeId">Its changeId.</param>
        internal static bool Keeps(string operation, string kind, string mutationOperation, string changeId, string admittedOperation, string admittedChangeId)
        {
            switch (operation)
            {
                case "session_status": case "change_status": case "bridge_info": return true;
                case "change_prepare": return !string.IsNullOrEmpty(kind) && !kind.StartsWith("library_", StringComparison.Ordinal);
                case "mutation_begin": return EngineeringChange(mutationOperation, changeId);
                case "mutation_dispatch": case "mutation_end": return EngineeringChange(admittedOperation, admittedChangeId);
                default: return false;
            }
        }

        private static bool EngineeringChange(string operation, string changeId) =>
            (operation == "change_apply" || operation == "change_restore") && !string.IsNullOrEmpty(changeId) && !changeId.StartsWith("lib-", StringComparison.Ordinal);

        /// <summary>What the kept content depends on besides the project: the kind (inconsistent data types refuse per kind,
        /// tag imports read block metadata only) and the target blocks (an inconsistent target refuses, a bystander does not).</summary>
        internal static string Variant(string kind, System.Collections.Generic.IEnumerable<string> targetBlockNames)
        {
            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (string name in targetBlockNames ?? new string[0]) names.Add((name ?? "").ToUpperInvariant());
            return kind + "\n" + string.Join("\n", names);
        }
    }
}
