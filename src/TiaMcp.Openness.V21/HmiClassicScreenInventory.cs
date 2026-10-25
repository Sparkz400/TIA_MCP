using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    // The same bounded traversal is used by the native adapter and offline fixtures.
    internal static class HmiClassicScreenInventory
    {
        private sealed class Scope
        {
            internal string Property, Segment, Collection, Role; internal bool Folders;
            internal Scope(string property, string segment, string collection, string role, bool folders = true)
            { Property = property; Segment = segment; Collection = collection; Role = role; Folders = folders; }
        }
        private static readonly Scope[] Scopes = {
            new Scope("ScreenFolder", "screens", "Screens", "regular"),
            new Scope("ScreenTemplateFolder", "screenTemplates", "ScreenTemplates", "template"),
            new Scope("ScreenPopupFolder", "screenPopups", "ScreenPopups", "popup"),
            // The native slide-in system folder has a fixed set of slide-ins and no Folders composition.
            new Scope("ScreenSlideinFolder", "screenSlideins", "ScreenSlideins", "slideIn", folders: false),
            new Scope("ScreenGlobalElements", "screenGlobalElements", null, "globalElements"),
            new Scope("ScreenOverview", "screenOverview", null, "overview")
        };

        internal static JArray Capture(string targetPath, Func<string, object> root,
            Func<object, string, IEnumerable> composition, Func<object, string> name,
            Func<bool> take, Func<object, string, string, string, string, bool> emit,
            Action<string, Exception> error, int maximumDepth = 32, string selectedPath = null)
        {
            var result = new JArray(); var paths = new HashSet<string>(StringComparer.Ordinal); bool stopped = false;
            foreach (var spec in Scopes)
            {
                string path = targetPath + "/" + spec.Segment;
                if (selectedPath != null && selectedPath != targetPath && selectedPath != path &&
                    !selectedPath.StartsWith(path + "/", StringComparison.Ordinal)) continue;
                var coverage = new JObject { ["role"] = spec.Role, ["property"] = spec.Property,
                    ["path"] = path, ["status"] = "complete", ["observed"] = 0 };
                result.Add(coverage);
                void Failed(string at, Exception failure)
                {
                    if ((string)coverage["status"] != "truncated") coverage["status"] = "unavailable";
                    error(at, failure);
                }
                bool Take()
                {
                    if (stopped || !take()) { stopped = true; coverage["status"] = "truncated"; return false; }
                    return true;
                }
                string Name(object item, string at)
                {
                    try { return name(item); }
                    catch (Exception failure) { Failed(at, failure); return null; }
                }
                bool Unique(string at)
                {
                    if (paths.Add(at)) return true;
                    Failed(at, new InvalidOperationException("Duplicate native screen/folder path; uniqueness is unavailable.")); return false;
                }
                void Emit(object item, string at, string parent, string nativeName)
                {
                    if (!Unique(at)) return;
                    try
                    {
                        if (!emit(item, at, parent, spec.Role, nativeName)) { stopped = true; coverage["status"] = "truncated"; }
                        else coverage["observed"] = (int)coverage["observed"] + 1;
                    }
                    catch (Exception failure) { Failed(at, failure); }
                }
                void Each(object owner, string collection, string at, Action<object> visit)
                {
                    if (stopped) return;
                    try
                    {
                        var values = composition(owner, collection);
                        if (values == null) throw new InvalidOperationException("Native screen composition is unavailable.");
                        var iterator = values.GetEnumerator();
                        if (iterator == null) throw new InvalidOperationException("Native screen enumerator is unavailable.");
                        try
                        {
                            while (iterator.MoveNext())
                            {
                                if (!Take()) break;
                                var item = iterator.Current;
                                if (item == null) { Failed(at, new InvalidOperationException("Null native screen/folder entry.")); continue; }
                                visit(item); if (stopped) break;
                            }
                        }
                        finally { (iterator as IDisposable)?.Dispose(); }
                    }
                    catch (Exception failure) { Failed(at + "/" + collection, failure); }
                }
                void Folder(object folder, string at, int depth)
                {
                    if (depth > maximumDepth)
                    {
                        coverage["status"] = "truncated";
                        error(at, new InvalidOperationException("Classic screen folder depth exceeds the bound.")); return;
                    }
                    Each(folder, spec.Collection, at, item => {
                        string nativeName = Name(item, at);
                        string child = HmiInspectionRules.NamedChildPath(at, nativeName);
                        if (child == null) { Failed(at, new InvalidOperationException("Native screen name is missing or unreadable.")); return; }
                        Emit(item, child, at, nativeName);
                    });
                    if (spec.Folders) Each(folder, "Folders", at, child => {
                        string nativeName = Name(child, at);
                        string childPath = HmiInspectionRules.NamedChildPath(at + "/folders", nativeName);
                        if (childPath == null) { Failed(at, new InvalidOperationException("Native screen folder name is missing or unreadable.")); return; }
                        if (Unique(childPath)) Folder(child, childPath, depth + 1);
                    });
                }
                if (!Take()) continue;
                object native;
                try { native = root(spec.Property); }
                catch (Exception failure) { Failed(path, failure); continue; }
                if (native == null)
                {
                    if (spec.Collection == null) coverage["status"] = "absent";
                    else Failed(path, new InvalidOperationException("Native screen folder is unavailable."));
                    continue;
                }
                if (spec.Collection == null) Emit(native, path, targetPath, Name(native, path));
                else Folder(native, path, 0);
            }
            return result;
        }
    }
}
