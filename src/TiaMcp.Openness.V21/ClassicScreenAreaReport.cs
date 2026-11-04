using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace TiaMcp.Model
{
    internal static class ClassicScreenAreaReport
    {
        internal static JObject Prototypes(IReadOnlyDictionary<string, XDocument> documents, ISet<string> selected = null)
        {
            var rows = new JArray();
            foreach (var screen in documents.Where(p => selected == null || selected.Contains(p.Key)))
                foreach (var item in ClassicScreenPreflight.Items(screen.Value))
                {
                    if (rows.Count == 20000) return new JObject { ["items"] = rows, ["complete"] = false, ["reason"] = "20000 item bound" };
                    rows.Add(new JObject { ["screenPath"] = screen.Key, ["sourceItem"] = ClassicScreenPreflight.Attribute(item, "ObjectName"), ["nativeType"] = item.Name.LocalName,
                        ["attributes"] = Scalars(item.Element("AttributeList")),
                        ["tags"] = new JArray(item.Descendants("Tag").Elements("Name").Select(t => t.Value).Distinct(StringComparer.Ordinal)),
                        ["captions"] = new JArray(item.Descendants("MultilingualText").SelectMany(t => t.Descendants("MultilingualTextItem").Select(c => new JObject {
                            ["compositionName"] = (string)t.Attribute("CompositionName"), ["culture"] = ClassicScreenPreflight.Attribute(c, "Culture"), ["text"] = c.Element("AttributeList")?.Element("Text")?.Value }))),
                        ["interfaceValues"] = new JArray(item.Descendants("Hmi.Screen.InterfacePropertySimple").Select(p => new JObject { ["name"] = ClassicScreenPreflight.Attribute(p, "Name"),
                            ["value"] = ClassicScreenPreflight.Attribute(p, "Value"), ["type"] = (string)p.Element("AttributeList")?.Element("Value")?.Attribute("Type"),
                            ["linked"] = p.Element("LinkList") != null })),
                        ["events"] = new JArray(item.Descendants("Hmi.Event.Event").Select(e => new JObject { ["name"] = ClassicScreenPreflight.Attribute(e, "Name"),
                            ["functions"] = new JArray(e.Descendants("Hmi.Event.FunctionListEntry").Select(f => new JObject { ["name"] = ClassicScreenPreflight.Attribute(f, "Name"),
                                ["parameters"] = new JArray(f.Descendants("Hmi.Event.FunctionListEntryParameter").Select(p => Scalars(p.Element("AttributeList")))) })) })),
                        ["visibility"] = new JArray(item.Descendants("Hmi.Dynamic.VisibilityAnimation").Select(a => Scalars(a.Element("AttributeList")))),
                        ["permissionReview"] = "Native permission and event trees are retained in the source artifact; tag names do not prove operating permission." });
                }
            return new JObject { ["items"] = rows, ["complete"] = true, ["runtimeVerified"] = false };
        }
        private static JObject Scalars(XElement fields) => new JObject((fields?.Elements() ?? Enumerable.Empty<XElement>()).Where(x => !x.HasElements)
            .Select(x => new JProperty(x.Name.LocalName, x.Value)));

        internal static JArray BindingFindings(IEnumerable<XDocument> tables, IReadOnlyDictionary<string,XDocument> types, ISet<string> roots)
        {
            var findings = new JArray();
            foreach(var table in tables) foreach(var tag in table.Descendants("Hmi.Tag.Tag"))
            {
                string root=ClassicScreenPreflight.Attribute(tag,"Name"), type=(string)tag.Element("LinkList")?.Element("DataType")?.Element("Name");
                if(root==null || roots!=null && !roots.Contains(root)) continue;
                string controller=(string)tag.Element("LinkList")?.Element("ControllerTag")?.Element("Name");
                if(type==null || controller==null) continue;
                if(!types.TryGetValue(type,out var declared)) { findings.Add(new JObject { ["code"]="TypeNotCaptured",["root"]=root,["datatype"]=type,["controllerTag"]=controller }); continue; }
                var native=ClassicScreenPreflight.NativeTags(new[]{new XDocument(new XElement("Document",new XElement(tag)))});
                foreach(var member in declared.Descendants().Where(x=>x.Name.LocalName=="Member"))
                {
                    var ancestors=member.AncestorsAndSelf().Reverse().Where(x=>x.Name.LocalName=="Member").ToArray();
                    if(ancestors.Any(x=>((string)x.Attribute("Datatype")??"").StartsWith("Array",StringComparison.OrdinalIgnoreCase))) continue;
                    string path=root+"."+string.Join(".",ancestors.Select(x=>(string)x.Attribute("Name")));
                    if(!native.Contains(path)) findings.Add(new JObject { ["code"]="HmiMemberMissing",["root"]=root,["datatype"]=type,["member"]=path,["controllerTag"]=controller });
                }
            }
            return findings;
        }

        internal static JObject Declarations(XDocument document)
        {
            var members = new JArray();
            foreach (var node in document.Descendants().Where(x => x.Name.LocalName == "Member"))
            {
                if (members.Count == 10000) return new JObject { ["members"] = members, ["complete"] = false };
                members.Add(new JObject { ["path"] = string.Join(".", node.AncestorsAndSelf().Reverse().Where(x => x.Name.LocalName == "Member").Select(x => (string)x.Attribute("Name"))),
                    ["datatype"] = (string)node.Attribute("Datatype"), ["section"] = (string)node.Ancestors().FirstOrDefault(x => x.Name.LocalName == "Section")?.Attribute("Name") });
            }
            // Call operands and constants are evidence for parameter/manual-ID review,
            // not inferred semantic mappings. Keep their owning native call intact.
            var calls = document.Descendants().Where(x => x.Name.LocalName == "Call" || x.Name.LocalName == "CallInfo").Take(257).ToArray();
            return new JObject { ["members"] = members, ["calls"] = new JArray(calls.Take(256).Select(x => x.ToString(SaveOptions.DisableFormatting))),
                ["complete"] = calls.Length <= 256, ["semanticMappingsVerified"] = false };
        }
    }
}
