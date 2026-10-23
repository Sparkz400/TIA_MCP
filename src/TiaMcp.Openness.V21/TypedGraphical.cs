#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaMcp.Openness.V21
{
    /// <summary>Pure typed graphical compiler. It never calls native APIs or writes a project.</summary>
    internal static partial class TypedGraphical
    {
        internal static readonly XNamespace Net = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
        internal static readonly XNamespace Sections = "http://www.siemens.com/automation/Openness/SW/Interface/v5";
        private static readonly HashSet<string> NumericTypes = new HashSet<string>(new[] { "SInt", "Int", "DInt", "LInt", "USInt", "UInt", "UDInt", "ULInt", "Real", "LReal" }, StringComparer.Ordinal);
        private static readonly HashSet<string> ScalarTypes = new HashSet<string>(NumericTypes.Concat(new[] { "Bool", "Byte", "Word", "DWord", "LWord", "Time", "LTime" }), StringComparer.Ordinal);
        private static readonly Regex KeyPattern = new Regex(@"^[A-Za-z_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant);

        internal static TypedGraphicalBuildResult Build(XDocument original, TypedGraphicalModel model, TypedGraphicalContext context)
        {
            if (model == null || context == null) Fail("InvalidTypedGraphicalModel", "A model and freshly resolved context are required.");
            if (model.SchemaVersion != 1) Fail("TypedGraphicalVersionUnsupported", "Only typed graphical schemaVersion 1 is supported.");
            if (model.Language != "LAD" && model.Language != "FBD") Fail("GraphicalLanguageMismatch", "The language must be exactly LAD or FBD.");
            if (context.TiaVersion != "V21") Fail("GraphicalTargetUnsupported", "This model compiler supports the V21 native format only.");
            if (context.Route != "standard" && context.Route != "softwareUnit" && context.Route != "namespace") Fail("GraphicalTargetUnsupported", "An explicit supported route is required.");
            if (model.Nodes == null || model.Connections == null || model.Nodes.Length == 0 || model.Nodes.Length > 1024 || model.Connections.Length > 4096)
                Fail("InvalidTypedGraphicalModel", "A network requires 1–1024 nodes and at most 4096 connections.");
            if (model.Nodes.Any(n => n == null || n.Key == null || !KeyPattern.IsMatch(n.Key)) || model.Nodes.Select(n => n.Key).Distinct(StringComparer.Ordinal).Count() != model.Nodes.Length)
                Fail("InvalidTypedGraphicalModel", "Logical node keys must be unique bounded identifiers.");
            if (model.Operation != "replaceNetwork" && model.Operation != "editNetwork" && model.Nodes.Any(n => n.NativeUid != null)) Fail("InvalidTypedGraphicalModel", "nativeUid is only accepted when editing an existing network.");
            var document = original == null ? CreateEnvelope(model) : new XDocument(original);
            EngineeringSources.BlockDeclarations(document);
            var block = document.Root.Elements().SingleOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            if (block == null) Fail("InvalidTypedGraphicalModel", "One exported target block is required.");
            if (block.Element("ObjectList") == null) Fail("InvalidSimaticML", "The native block has no ObjectList preservation envelope.");
            if (block.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != model.Language)
                Fail("GraphicalLanguageMismatch", "Typed editing never converts block or network languages.");
            if (model.Operation == "editNetwork") return EditOperands(document, original, model, context);
            if (model.Operation != "insertNetwork" && model.Operation != "replaceNetwork" && model.Operation != "createBlock")
                Fail("InvalidTypedGraphicalModel", "operation must be insertNetwork, replaceNetwork, editNetwork or createBlock.");
            if (model.Operation == "createBlock" && original != null) Fail("InvalidTypedGraphicalModel", "createBlock cannot overwrite an existing native block.");
            XElement target = null;
            if (model.Operation != "createBlock")
            {
                string selectedId = model.Operation == "insertNetwork" ? model.InsertAfterNetworkId : model.NetworkId;
                target = document.Descendants("SW.Blocks.CompileUnit").SingleOrDefault(e => (string)e.Attribute("ID") == selectedId);
                bool firstNetwork = model.Operation == "insertNetwork" && selectedId == null && !document.Descendants("SW.Blocks.CompileUnit").Any();
                if (target == null && !firstNetwork) Fail("NetworkNotFound", "The exact compile-unit ID was not found.");
                if (target != null && target.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value != model.Language) Fail("GraphicalLanguageMismatch", "The selected network has a different language.");
                if (model.Operation == "replaceNetwork") RequireReplaceable(target, context.Profiles);
            }
            var allocator = new NativeIds(document, model.Operation == "replaceNetwork" ? target?.Element("AttributeList")?.Element("NetworkSource")?.Element(Net + "FlgNet") : null);
            string networkId = model.Operation == "replaceNetwork" ? model.NetworkId : allocator.ObjectId("network");
            var nodes = model.Nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
            var pins = new Dictionary<string, Dictionary<string, TypedGraphicalPin>>(StringComparer.Ordinal);
            var native = new Dictionary<string, XElement>(StringComparer.Ordinal);
            var usedOperands = new HashSet<string>(StringComparer.Ordinal);
            var profiles = new List<string>();
            var effects = new List<TypedGraphicalEffect>();
            var instanceUses = new Dictionary<string, List<TypedGraphicalNode>>(StringComparer.Ordinal);
            foreach (var node in model.Nodes.Where(n => n.Operand != null)) usedOperands.Add(node.Operand);
            foreach (var connection in model.Connections.Where(c => c != null))
                foreach (var endpoint in new[] { connection.From, connection.To }.Where(e => e?.Node != null && e.Node.StartsWith("$", StringComparison.Ordinal))) usedOperands.Add(endpoint.Node.Substring(1));
            // Allocate in native document order: accesses, instructions (including instance IDs), then wires.
            foreach (string key in usedOperands.OrderBy(k => k, StringComparer.Ordinal)) allocator.Uid("operand:" + key);
            foreach (var node in model.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
            {
                string uid = node.Kind == "powerrail" ? null : allocator.Uid("node:" + node.Key);
                var profile = SelectProfile(node, model.Language, context);
                profiles.Add(profile.Id);
                pins[node.Key] = BuildPins(node, profile, context);
                if (node.Kind != "powerrail") native[node.Key] = BuildPart(node, uid, profile, context, allocator);
                if (node.Operand != null)
                {
                    if (node.Kind != "contact" && node.Kind != "coil" && node.Kind != "assign") Fail("InvalidTypedGraphicalModel", "operand is only valid on contact, coil and assign nodes.");
                    var operand = ResolveOperand(context, node.Operand);
                    RequireType("Bool", operand.DataType);
                    if (node.Kind != "contact" && !operand.Writable) Fail("OperandNotWritable", "Coil and assignment operands must be writable: " + node.Operand);
                    usedOperands.Add(node.Operand);
                }
                else if (node.Kind == "contact" || node.Kind == "coil" || node.Kind == "assign") Fail("UnresolvedOperand", "Contact, coil and assignment nodes require an explicit operand key.");
                if (node.Kind == "call")
                {
                    var callee = ResolveCallee(context, node.CalleePath);
                    effects.Add(new TypedGraphicalEffect { Kind = "dependency", Identity = callee.Path, SourceSha256 = callee.InterfaceHash, Description = "Exact " + callee.BlockType + " callee interface dependency." });
                }
                if (node.Instance != null)
                {
                    var instance = ResolveInstance(context, node, profile);
                    if (!instanceUses.ContainsKey(instance.Identity)) instanceUses[instance.Identity] = new List<TypedGraphicalNode>();
                    instanceUses[instance.Identity].Add(node);
                    effects.Add(new TypedGraphicalEffect { Kind = "instanceDependency", Identity = instance.Identity, SourceSha256 = instance.DeclarationHash, Description = "Explicit instance storage; no internal IEC offsets are synthesized." });
                }
                else if (node.Kind == "timer" || node.Kind == "counter" || (node.Kind == "call" && ResolveCallee(context, node.CalleePath).BlockType == "FB"))
                    Fail("GraphicalInstanceRequired", "Stateful instructions and FB calls require exact instance storage.");
            }
            if (instanceUses.Values.Any(v => v.Count > 1 && v.Any(n => !n.AllowSharedInstance))) Fail("GraphicalSharedInstance", "Every stateful node sharing storage must explicitly allow shared instance use.");
            ValidateConnections(model, context, nodes, pins, usedOperands, instanceUses);
            var parts = new XElement(Net + "Parts");
            foreach (var key in usedOperands.OrderBy(k => k, StringComparer.Ordinal))
            {
                var operand = ResolveOperand(context, key);
                parts.Add(BuildAccess(operand, allocator.Uid("operand:" + key)));
                effects.Add(new TypedGraphicalEffect { Kind = "operandDependency", Identity = operand.Identity, SourceSha256 = operand.DeclarationHash, Description = operand.Scope + " " + operand.DataType + " operand." });
            }
            foreach (var node in model.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal)) if (native.ContainsKey(node.Key)) parts.Add(native[node.Key]);
            var wires = new XElement(Net + "Wires");
            // A native output pin appears once; fan-out is additional endpoints on that wire.
            foreach (var group in model.Connections.GroupBy(c => EndpointKey(c.From), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var wire = new XElement(Net + "Wire", new XAttribute("UId", allocator.Uid("wire:" + group.Key)), Endpoint(group.First().From, nodes, allocator));
                foreach (var connection in group.OrderBy(c => EndpointKey(c.To), StringComparer.Ordinal)) wire.Add(Endpoint(connection.To, nodes, allocator));
                wires.Add(wire);
            }
            foreach (var node in model.Nodes.Where(n => n.Operand != null).OrderBy(n => n.Key, StringComparer.Ordinal))
                wires.Add(new XElement(Net + "Wire", new XAttribute("UId", allocator.Uid("wire:operand:" + node.Key)),
                    new XElement(Net + "IdentCon", new XAttribute("UId", allocator.Mapping["operand:" + node.Operand])), Pin(allocator.Mapping["node:" + node.Key], "operand")));
            var flgNet = new XElement(Net + "FlgNet", parts, wires);
            if (model.Operation == "replaceNetwork")
            {
                var previous = target.Element("AttributeList").Element("NetworkSource").Element(Net + "FlgNet");
                PreserveMatchingIds(previous, flgNet, model, allocator);
                if (!XNode.DeepEquals(Shape(previous), Shape(flgNet)))
                    CanonicalizeNetworkIds(flgNet, previous, model, pins, allocator, effects, networkId);
                target.Element("AttributeList").Element("NetworkSource").Elements().Single().ReplaceWith(flgNet);
            }
            else
            {
                CanonicalizeNetworkIds(flgNet, null, model, pins, allocator, effects, networkId);
                var unit = CreateCompileUnit(flgNet, networkId, model.Language, context.NetworkTemplateXml, allocator);
                if (model.Operation == "insertNetwork") RequireInsertionIdPreservation(document, block, target, unit);
                if (target != null) target.AddAfterSelf(unit); else block.Element("ObjectList").Add(unit);
            }
            effects.Insert(0, new TypedGraphicalEffect { Kind = model.Operation, Identity = networkId, Description = "Native " + model.Language + " network " + model.Operation + "; compilation and complete readback required." });
            return Result(document, original, networkId, allocator.Mapping, profiles, effects);
        }

        private static TypedGraphicalBuildResult Result(XDocument document, XDocument original, string networkId, Dictionary<string, string> ids, IEnumerable<string> profiles, IEnumerable<TypedGraphicalEffect> effects)
        {
            return new TypedGraphicalBuildResult { Document = document, NetworkId = networkId, IdMapping = ids, Profiles = profiles.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(), Effects = effects.ToArray(),
                SourceSha256 = original == null ? null : Hash(Xml(original)), ResultSha256 = Hash(Xml(document)),
                Diagnostics = new[] { new TypedGraphicalDiagnostic { Code = "NativeValidationRequired", Severity = "information", Message = "Typed validation does not establish native acceptance, preservation, persistence, recovery or runtime behavior. Apply through the existing compiled change workflow." } } };
        }
        private static void RequireType(string expected, string actual) { if (!string.Equals(expected, actual, StringComparison.Ordinal)) Fail("OperandTypeMismatch", "Expected exact type " + expected + ", observed " + actual + "; no implicit conversion is allowed."); }
        private static void Fail(string code, string message) { throw new BridgeException(code, message); }
        internal static string Hash(string text) { using (var hash = SHA256.Create()) return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
        private static string Xml(XNode node) => node.ToString(SaveOptions.DisableFormatting);
    }
}
