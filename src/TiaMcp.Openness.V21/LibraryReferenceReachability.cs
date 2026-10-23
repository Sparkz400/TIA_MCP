using System;
using System.Collections.Generic;
using System.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed class LibraryReferenceEdge
    {
        internal LibraryReferenceEdge(string callerPath, string calleePath)
        {
            CallerPath = callerPath;
            CalleePath = calleePath;
        }

        internal string CallerPath { get; }
        internal string CalleePath { get; }
    }

    /// <summary>
    /// Proves native reference reachability from trusted program entry points.
    /// A cycle or an incoming reference alone is not evidence that an object is used.
    /// Object/root/use inventories must be complete; supplied edges must be positively
    /// resolved native references. Missing possible edges never prove a required use.
    /// </summary>
    internal static class LibraryReferenceReachability
    {
        private const int MaximumObjects = 50000;
        private const int MaximumEdges = 200000;

        internal static string[] RequireReachable(
            IEnumerable<string> knownObjectPaths,
            IEnumerable<string> trustedRootPaths,
            IEnumerable<LibraryReferenceEdge> edges,
            IEnumerable<string> baselineLibraryUsePaths,
            bool complete)
        {
            if (!complete) throw new ArgumentException("Reference evidence is incomplete or truncated.");
            var known = ReadPaths(knownObjectPaths, "Known object paths");
            var roots = ReadPaths(trustedRootPaths, "Trusted root paths");
            var required = ReadPaths(baselineLibraryUsePaths, "Baseline library-use paths");
            if (roots.Count == 0) throw new ArgumentException("No trusted program entry point is available.");
            foreach (var root in roots)
                if (!known.Contains(root)) throw new ArgumentException("Unknown trusted root path: " + root);
            foreach (var path in required)
                if (!known.Contains(path)) throw new ArgumentException("Unknown baseline library-use path: " + path);
            if (edges == null) throw new ArgumentNullException(nameof(edges));

            var outgoing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            int edgeCount = 0;
            foreach (var edge in edges)
            {
                if (++edgeCount > MaximumEdges) throw new ArgumentException("Reference edge limit exceeded; evidence cannot be verified completely.");
                if (edge == null) throw new ArgumentException("Reference evidence contains a missing edge.");
                RequirePath(edge.CallerPath, "Reference caller");
                RequirePath(edge.CalleePath, "Reference callee");
                if (!known.Contains(edge.CallerPath) || !known.Contains(edge.CalleePath))
                    throw new ArgumentException("Reference edge has an unresolved object path: " + edge.CallerPath + " -> " + edge.CalleePath);
                if (!outgoing.TryGetValue(edge.CallerPath, out var callees))
                    outgoing.Add(edge.CallerPath, callees = new HashSet<string>(StringComparer.Ordinal));
                // Native cross references can contain multiple locations for the same pair.
                callees.Add(edge.CalleePath);
            }

            var reachable = new HashSet<string>(roots, StringComparer.Ordinal);
            var pending = new Queue<string>(roots);
            while (pending.Count != 0)
            {
                var caller = pending.Dequeue();
                if (!outgoing.TryGetValue(caller, out var callees)) continue;
                foreach (var callee in callees)
                    if (reachable.Add(callee)) pending.Enqueue(callee);
            }

            var unreachable = required.Where(path => !reachable.Contains(path)).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (unreachable.Length != 0)
                throw new ArgumentException("Baseline library uses lack a trusted-root reference path (" + unreachable.Length + "): " + string.Join(", ", unreachable.Take(10)));
            return reachable.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        private static HashSet<string> ReadPaths(IEnumerable<string> paths, string field)
        {
            if (paths == null) throw new ArgumentNullException(field);
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths)
            {
                RequirePath(path, field);
                if (!result.Add(path)) throw new ArgumentException(field + " contains a duplicate exact path: " + path);
                if (result.Count > MaximumObjects) throw new ArgumentException(field + " exceeds the object limit; evidence cannot be verified completely.");
            }
            return result;
        }

        private static void RequirePath(string path, string field)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path[0] != '/' || path.Length == 1
                || path.Any(char.IsControl) || path.IndexOf('\\') >= 0
                || path.Substring(1).Split('/').Any(segment => segment.Length == 0 || segment == "." || segment == ".."))
                throw new ArgumentException(field + " must contain canonical exact object paths.");
        }
    }
}
