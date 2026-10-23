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
            var templates = (catalog["multiInstanceTemplates"] as JArray ?? new JArray()).OfType<JObject>().Where(t => t.Value<string>("tiaVersion") == "V21" && t.Value<string>("cpuFamily") == cpu && t.Value<string>("route") == route);
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

        private void PrepareTypedDependentInstance(TypedResolutionCapture capture, JObject dependency, string callerName, XElement declaration)
        {
            string catalogPath = TypedCatalogPath();
            string manifestPath = Path.Combine(Path.GetDirectoryName(catalogPath), "instance-effects.v1.json");
            if (!File.Exists(manifestPath)) throw new BridgeException("DependencyProfileUnavailable", "The deployed catalog lacks native dependent instance effects.");
            var profile = JObject.Parse(File.ReadAllText(manifestPath));
            if (profile.Value<int>("schemaVersion") != 1 || profile.Value<string>("tiaVersion") != "V21" || profile.Value<string>("cpuFamily") != TypedCpuIdentifier(capture.Plc)
                || profile.Value<string>("route") != TypedRoute(ResolveSoftwareScope(capture.Plc, capture.Change.ScopePath), capture.Change.Namespace))
                throw new BridgeException("DependencyProfileUnavailable", "No exact target dependent instance layout profile exists.");
            capture.Change.TypedDependencies.Add(new JObject { ["kind"] = "profileCatalog", ["artifact"] = ChangeArtifact(manifestPath) });
            var resources = (JObject)profile["artifacts"];
            var caller = ReadTypedProfileResource(capture, catalogPath, resources["afterCaller"].Value<string>("path"), resources["afterCaller"].Value<string>("sha256"));
            var instance = ReadTypedProfileResource(capture, catalogPath, resources["afterInstance"].Value<string>("path"), resources["afterInstance"].Value<string>("sha256"));
            // Multiple new members compose over the previous prepared effect. The
            // native baseline remains immutable for stale/recovery checks.
            var prior = dependency["expectedArtifact"] ?? dependency["artifact"];
            var current = EngineeringSources.ReadXml(File.ReadAllText(prior.Value<string>("path")));
            var expected = TypedGraphicalMultiInstanceTemplate.AppendDependent(current, callerName, declaration,
                TypedTopStaticMember(caller, profile.Value<string>("memberName")), TypedTopStaticMember(instance, profile.Value<string>("memberName")));
            string output = Path.Combine(capture.Change.Directory, "dependent-effect-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(output, expected.ToString(SaveOptions.DisableFormatting), new System.Text.UTF8Encoding(true));
            dependency["expectedArtifact"] = ChangeArtifact(output);
        }
    }
}
