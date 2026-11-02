using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static XDocument ReadTypedProfileResource(TypedResolutionCapture capture, string catalogPath, string relative, string expectedHash)
        {
            string path = TypedResourcePath(catalogPath, relative);
            if (!File.Exists(path) || ChangeFileHash(path) != expectedHash)
                throw new BridgeException("ProfileCatalogInvalid", "A captured declaration profile resource is missing or changed.");
            capture.Change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(path) });
            return EngineeringSources.ReadXml(File.ReadAllText(path));
        }

        private XElement PrepareTypedMultiDeclaration(TypedResolutionCapture capture, TypedGraphicalCallee callee, string name, string dataType)
        {
            string catalogPath = TypedCatalogPath();
            var catalog = JObject.Parse(File.ReadAllText(catalogPath));
            string cpu = TypedCpuIdentifier(capture.Plc);
            string route = TypedRoute(ResolveSoftwareScope(capture.Plc, capture.Change.ScopePath), capture.Change.Namespace);
            var templates = TypedGraphicalInstanceTemplate.Admitted(catalog, "multiInstanceTemplates", cpu, route, TypedQualificationScratch(capture.Plc.Project));
            BridgeException last = null;
            foreach (var template in templates)
            {
                var owner = ReadTypedProfileResource(capture, catalogPath, template.Value<string>("path"), template.Value<string>("sha256"));
                var reference = ReadTypedProfileResource(capture, catalogPath, template.Value<string>("referenceCalleePath"), template.Value<string>("referenceCalleeSha256"));
                var declaration = TypedTopStaticMember(owner, template.Value<string>("memberName"));
                try { return TypedGraphicalMultiInstanceTemplate.Create(name, dataType, capture.Export(capture.Block(callee.Path)), reference, declaration); }
                catch (BridgeException ex) { last = ex; }
            }
            throw new BridgeException("InstanceCreationProfileUnavailable", "No complete captured multi-instance profile covers the requested FB declarations: " + last?.Message);
        }

        private static XElement TypedTopStaticMember(XDocument source, string name)
        {
            var owner = source.Root.Elements().Single(e => e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.Ordinal));
            return owner.Element("AttributeList").Element("Interface").Elements().Single(e => e.Name.LocalName == "Sections")
                .Elements().Single(e => e.Name.LocalName == "Section" && e.Attribute("Name")?.Value == "Static")
                .Elements().SingleOrDefault(e => e.Name.LocalName == "Member" && e.Attribute("Name")?.Value == name)
                ?? throw new BridgeException("ProfileCatalogInvalid", "Captured storage member is missing or ambiguous.");
        }

        private JObject ReadTypedEffectManifest(TypedResolutionCapture capture, string catalogPath, string route, string name)
        {
            string manifestPath = Path.Combine(Path.GetDirectoryName(catalogPath), name);
            if (!File.Exists(manifestPath)) throw new BridgeException("DependencyProfileUnavailable", "The deployed catalog lacks native dependent instance effects: " + name);
            var profile = JObject.Parse(File.ReadAllText(manifestPath));
            if (profile.Value<int>("schemaVersion") != 1 || profile.Value<string>("tiaVersion") != "V21" || profile.Value<string>("cpuFamily") != TypedCpuIdentifier(capture.Plc)
                || profile.Value<string>("route") != route)
                throw new BridgeException("DependencyProfileUnavailable", "No exact target dependent instance layout profile exists.");
            capture.Change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(manifestPath) });
            return profile;
        }

        // The direct instance-DB effect of a caller FB gaining a multi-instance of the PLC component FB (captured on the standard
        // route, local.typed.component-effect-capture.20261102). A qualificationOnly pair serves only a --component-qualification
        // worker's own component scratch until a combined apply verifies it.
        internal const string ComponentEffectManifest = "instance-effects-component.v1.json";
        private JObject TypedComponentEffect(TypedResolutionCapture capture, string catalogPath, string route)
        {
            if (route != "standard" || !File.Exists(Path.Combine(Path.GetDirectoryName(catalogPath), ComponentEffectManifest))) return null;
            var profile = ReadTypedEffectManifest(capture, catalogPath, route, ComponentEffectManifest);
            return profile.Value<bool?>("qualificationOnly") == true && !TypedQualificationScratch(capture.Plc.Project) ? null : profile;
        }

        // transitive: the dependent is a parent FB or its InstanceDB that embeds the changed FB as a nested multi-instance.
        private void PrepareTypedDependentInstance(TypedResolutionCapture capture, JObject dependency, string callerName, XElement declaration, bool transitive = false)
        {
            string catalogPath = TypedCatalogPath();
            string route = TypedRoute(ResolveSoftwareScope(capture.Plc, capture.Change.ScopePath), capture.Change.Namespace);
            string type = (string)declaration.Attribute("Datatype");
            bool system = TypedGraphicalMultiInstanceTemplate.SystemInstanceTypes.Contains(type);
            string cpu = TypedCpuIdentifier(capture.Plc);
            var profile = ReadTypedEffectManifest(capture, catalogPath, route, TypedGraphicalMultiInstanceTemplate.EffectManifestName(route, system, cpu));
            if (transitive) ReadTypedEffectManifest(capture, catalogPath, route, TypedGraphicalMultiInstanceTemplate.TransitiveManifestName(route, cpu));
            var resources = (JObject)profile["artifacts"];
            var caller = ReadTypedProfileResource(capture, catalogPath, resources["afterCaller"].Value<string>("path"), resources["afterCaller"].Value<string>("sha256"));
            var instance = ReadTypedProfileResource(capture, catalogPath, resources["afterInstance"].Value<string>("path"), resources["afterInstance"].Value<string>("sha256"));
            // Multiple new members compose over the previous prepared effect. The
            // native baseline remains immutable for stale/recovery checks.
            var prior = dependency["expectedArtifact"] ?? dependency["artifact"];
            var current = EngineeringSources.ReadXml(File.ReadAllText(prior.Value<string>("path")));
            XElement member;
            if (system)
            {
                string captured = (profile["members"] as JObject)?.Value<string>(type)
                    ?? throw new BridgeException("DependencyProfileUnavailable", "No captured IEC effect covers " + type + ".");
                member = TypedGraphicalMultiInstanceTemplate.SystemDependentMember(declaration, TypedTopStaticMember(caller, captured), TypedTopStaticMember(instance, captured));
            }
            else
            {
                string memberName = profile.Value<string>("memberName");
                try { member = TypedGraphicalMultiInstanceTemplate.DependentMember(declaration, TypedTopStaticMember(caller, memberName), TypedTopStaticMember(instance, memberName)); }
                catch (BridgeException) when (!transitive)
                {
                    // The generic pair is scalar; the component FB's structured (UDT) member maps only onto its own captured pair.
                    var component = TypedComponentEffect(capture, catalogPath, route);
                    if (component == null) throw;
                    var parts = (JObject)component["artifacts"]; memberName = component.Value<string>("memberName");
                    member = TypedGraphicalMultiInstanceTemplate.DependentMember(declaration,
                        TypedTopStaticMember(ReadTypedProfileResource(capture, catalogPath, parts["afterCaller"].Value<string>("path"), parts["afterCaller"].Value<string>("sha256")), memberName),
                        TypedTopStaticMember(ReadTypedProfileResource(capture, catalogPath, parts["afterInstance"].Value<string>("path"), parts["afterInstance"].Value<string>("sha256")), memberName));
                }
            }
            var expected = transitive ? TypedGraphicalMultiInstanceTemplate.AppendNested(current, callerName, member)
                : TypedGraphicalMultiInstanceTemplate.AppendDirect(current, callerName, member);
            string output = Path.Combine(capture.Change.Directory, "dependent-effect-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(output, expected.ToString(SaveOptions.DisableFormatting), new System.Text.UTF8Encoding(true));
            dependency["expectedArtifact"] = ChangeArtifact(output);
        }
    }
}
