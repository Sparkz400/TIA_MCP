using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using TiaMcp.Analysis;

namespace TiaMcp.Openness.V21
{
    // Pure decisions shared by native capture and dependency revalidation.
    // Callers must supply complete inventories; native traversal budgets stay at the boundary.
    internal static class TypedGraphicalResolution
    {
        internal static T Exact<T>(IEnumerable<T> inventory, Func<T, bool> matches, string code, string message)
        {
            var found = inventory.Where(matches).Take(2).ToArray();
            if (found.Length != 1) throw new BridgeException(code, message);
            return found[0];
        }

        internal static void RequireUniqueSymbol(IEnumerable<string> names, string name, string code = "AmbiguousOperand")
        {
            if (string.IsNullOrEmpty(name) || names.Count(value => string.Equals(value, name, StringComparison.Ordinal)) != 1)
                throw new BridgeException(code, "The emitted symbol no longer has one exact declaration in the complete identity index: " + name);
        }

        internal static T ResolveType<T>(IEnumerable<T> inventory, string declaredType, string declarationNamespace,
            string scopePath, Func<T, string> name, Func<T, string> typeNamespace, Func<T, string> scope)
        {
            string qualified = declaredType.Contains(".") || string.IsNullOrEmpty(declarationNamespace)
                ? declaredType : declarationNamespace + "." + declaredType;
            return Exact(inventory, type => scope(type) == scopePath &&
                (string.IsNullOrEmpty(typeNamespace(type)) ? name(type) : typeNamespace(type) + "." + name(type)) == qualified,
                "TypeUnresolved", "A named UDT member lacks an exact same-scope native type identity: " + qualified);
        }

        internal static OfflineEvidence.InterfaceMember ResolveMember(XDocument source, string[] path, string ownerPath,
            string declarationNamespace, Func<string, string, Tuple<string, XDocument>> resolveType)
        {
            if (path == null || path.Length == 0) throw new BridgeException("InvalidOperand", "Member path is empty.");
            var roots = Members(source).Where(member => member.Path.Length == 1).ToArray();
            var selected = default(OfflineEvidence.InterfaceMember);
            for (int index = 0; index < path.Length; index++)
            {
                string component = path[index];
                selected = Exact(roots, member => member.Name == component, "OperandNotFound",
                    "Exact member path is missing or ambiguous in " + ownerPath);
                if (selected.Array != null)
                    throw new BridgeException(selected.Array.BoundsResolved ? "UnsupportedIndexing" : "UnresolvedArrayBounds",
                        "Array operands require a separately validated indexing profile.");
                roots = selected.Members.ToArray();
                if (index < path.Length - 1 && selected.DeclaredType != null)
                {
                    var declaration = resolveType(selected.DeclaredType, declarationNamespace);
                    // Inline expansions cannot substitute for an independently selected UDT.
                    // Nested unqualified names belong to that UDT's namespace, not the caller's.
                    declarationNamespace = declaration.Item1 ?? "";
                    roots = Members(declaration.Item2).Where(member => member.Path.Length == 1).ToArray();
                }
            }
            return selected ?? throw new BridgeException("InvalidOperand", "Member path is empty.");
        }

        internal static OfflineEvidence.InterfaceMember[] Members(XDocument source)
        {
            int remaining = 50000;
            bool truncated = false;
            var declarations = OfflineEvidence.NormalizeInterface(source,
                () => { if (remaining-- > 0) return true; truncated = true; return false; }, () => truncated = true);
            if (truncated) throw new BridgeException("TraversalLimit", "Declaration resolution was incomplete.");
            return declarations.Sections.SelectMany(section => Flatten(section.Members)).ToArray();
        }

        private static IEnumerable<OfflineEvidence.InterfaceMember> Flatten(IEnumerable<OfflineEvidence.InterfaceMember> members)
        {
            foreach (var member in members)
            {
                yield return member;
                foreach (var child in Flatten(member.Members)) yield return child;
            }
        }
    }
}
