using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

static class ClassicScreenWorkflowTests
{
    static void Assert(bool value) { if (!value) throw new Exception("Workflow assertion failed."); }
    static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected refusal."); }
    static XDocument Screen() => XDocument.Parse("<Document><Hmi.Screen.Screen ID='0'><AttributeList><Name>Main</Name><Width>800</Width><Height>480</Height></AttributeList><ObjectList><Hmi.Screen.ScreenLayer ID='1'><ObjectList><Hmi.Screen.Rectangle ID='2'><AttributeList><ObjectName>Prototype</ObjectName><Left>30</Left><Top>40</Top><Width>120</Width><Height>60</Height></AttributeList><ObjectList><Hmi.Event.Event ID='3'><Unknown>preserve</Unknown></Hmi.Event.Event></ObjectList></Hmi.Screen.Rectangle></ObjectList></Hmi.Screen.ScreenLayer></ObjectList></Hmi.Screen.Screen></Document>");
    static JObject Payload() => new() { ["schemaVersion"] = 1, ["operation"] = "classic_xml_import", ["expectedContentHash"] = new string('a',64),
        ["target"] = new JObject { ["family"] = "classic", ["projectCopyId"] = "copy", ["sessionGeneration"] = "session", ["hmiPath"] = "/hmi", ["objectPath"] = "/hmi/screens/Main", ["kind"] = "hmiScreen" }, ["xml"] = Screen().ToString() };
    public static void Run(Action<string,Action> check)
    {
        check("Compact receipts retain proof hashes and counts without mutating the full journal", () => {
            var full=new JObject { ["preview"]=new JObject { ["payload"]=Payload() }, ["contentHash"]="hash", ["artifacts"]=new JObject { ["screen"]="retained" } };
            string before=full.ToString();var compact=ClassicScreenReceipts.Compact(full);
            Assert(full.ToString()==before && compact["preview"]["payload"]["xml"]==null && compact.Value<int>("artifactCount")==1 && compact.Value<string>("contentHash")=="hash");
        });
        check("Lease observations share successful reads only within a live invalidation interval", () => {
            var lease = new HmiLeaseObservations(); int reads = 0;
            int Read() => ++reads;
            Assert(lease.Get("a", Read) == 1 && lease.Get("a", Read) == 1 && reads == 1);
            lease.Invalidate(); Assert(lease.Get("a", Read) == 2);
            Assert(new HmiLeaseObservations().Get("a", Read) == 3);
            try { lease.Get<int>("failure", () => throw new InvalidOperationException()); } catch (InvalidOperationException) { }
            Assert(lease.Get("failure", Read) == 4);
        });
        check("Batches accept eight distinct screen identities and reject nine", () => {
            var payload = Payload(); var extra = new JArray(); payload["additionalScreens"] = extra;
            for (int i=1;i<8;i++) { var member = Payload(); ((JObject)member["target"])["objectPath"] = "/hmi/screens/Other"+i; extra.Add(new JObject { ["target"] = member["target"].DeepClone(), ["xml"] = member["xml"].DeepClone() }); }
            HmiEngineeringRules.RequireRequest(payload); HmiEngineeringRules.RequireKind("hmi_classic_screen_edit", payload);
            Assert(ClassicScreenBatchRules.Members(payload).Length == 8);
            extra.Add(extra[0].DeepClone()); Reject(() => ClassicScreenBatchRules.Members(payload));
        });
        check("Batch scope rejects duplicate, cross-HMI, stale-generation, non-screen and extra fields", () => {
            foreach (string damage in new[] { "duplicate", "hmiPath", "projectCopyId", "sessionGeneration", "family", "role", "extra", "xml" }) {
                var payload=Payload(); var target=(JObject)payload["target"].DeepClone(); target["objectPath"]="/hmi/screens/Other";
                var member=new JObject { ["target"]=target, ["xml"]=payload["xml"].DeepClone() }; payload["additionalScreens"]=new JArray(member);
                if(damage=="duplicate") target["objectPath"]="/hmi/screens/Main";
                else if(damage=="role") target["objectPath"]="/hmi/screenTemplates/Other";
                else if(damage=="extra") member["skipValidation"]=true;
                else if(damage=="xml") member["xml"]="<Document><Hmi.Tag.TagTable><AttributeList><Name>T</Name></AttributeList></Hmi.Tag.TagTable></Document>";
                else target[damage]="different";
                Reject(()=>ClassicScreenBatchRules.Members(payload));
            }
            var wrong=Payload(); wrong["additionalScreens"]=new JArray(); Reject(()=>HmiEngineeringRules.RequireKind("hmi_classic_import",wrong));
        });
        check("Native tag evidence preserves quoted roots, structure names and exact array indices", () => {
            var tags=ClassicScreenPreflight.NativeTags(new[] { XDocument.Parse("<Document><Hmi.Tag.Tag><AttributeList><Name>DB.HMI</Name></AttributeList><ObjectList><Hmi.Tag.TagStructureMember><AttributeList><Name>Belts</Name></AttributeList><ObjectList><Hmi.Tag.TagArrayMember><AttributeList><Name>[0]</Name></AttributeList><ObjectList><Hmi.Tag.TagStructureMember><AttributeList><Name>Ready</Name></AttributeList></Hmi.Tag.TagStructureMember></ObjectList></Hmi.Tag.TagArrayMember></ObjectList></Hmi.Tag.TagStructureMember></ObjectList></Hmi.Tag.Tag></Document>") });
            Assert(tags.Contains("\"DB.HMI\".Belts[0].Ready") && !tags.Contains("\"DB.HMI\".Belts[1].Ready"));
            var original=Screen(); var candidate=Screen(); ClassicScreenPreflight.Items(candidate).Single().Add(new XElement("LinkList",new XElement("Tag", new XElement("Name","\"DB.HMI\".Belts[0].Ready"))));
            ClassicScreenPreflight.Check(candidate,original,new[]{original},tags);
            candidate.Descendants("Tag").Single().Element("Name").Value="\"DB.HMI\".Belts[1].Ready";
            Reject(()=>ClassicScreenPreflight.Check(candidate,original,new[]{original},tags));
        });
        check("Preflight detects duplicate names/IDs, geometry and native composition order", () => {
            foreach(string damage in new[]{"name","id","bounds","overflow","order"}) {
                var original=Screen();var candidate=Screen();var item=ClassicScreenPreflight.Items(candidate).Single();
                if(damage=="name") item.Parent.Add(new XElement(item));
                if(damage=="id") item.SetAttributeValue("ID","0");
                if(damage=="bounds") item.Element("AttributeList").SetElementValue("Left",799);
                if(damage=="overflow") item.Element("AttributeList").SetElementValue("Width",int.MaxValue);
                if(damage=="order") item.Element("ObjectList").Add(new XElement("Hmi.Globalization.Font"),new XElement("Hmi.Dynamic.VisibilityAnimation"));
                Reject(()=>ClassicScreenPreflight.Check(candidate,original,new[]{original},new HashSet<string>()));
            }
        });
        check("Composer copies native events and explicitly changes scalar placement without mutating sources", () => {
            var original=Screen(); string before=original.ToString();
            var request=new JObject { ["components"]=new JArray(new JObject { ["sourceScreenPath"]="/hmi/screens/Main", ["sourceItem"]="Prototype",["name"]="Clone",["attributes"]=new JObject { ["Left"]=200 } }) };
            var result=ClassicScreenComposer.Compose(original,new Dictionary<string,XDocument>{{"/hmi/screens/Main",original}},request);
            Assert(original.ToString()==before && ClassicScreenPreflight.Items(result).Count()==2);
            Assert(result.Descendants("Unknown").All(x=>x.Value=="preserve") && result.Descendants("Unknown").Count()==2);
            ClassicScreenPreflight.Check(result,original,new[]{original},new HashSet<string>());
            request["components"][0]["attributes"]["InventedAttribute"]=true;
            Reject(()=>ClassicScreenComposer.Compose(original,new Dictionary<string,XDocument>{{"/hmi/screens/Main",original}},request));
        });
        check("Composer normalizes only known visibility ordering and refuses local link remapping", () => {
            var original=Screen();var list=ClassicScreenPreflight.Items(original).Single().Element("ObjectList");
            list.Add(new XElement("Hmi.Globalization.Font"),new XElement("Hmi.Dynamic.VisibilityAnimation"));
            var result=ClassicScreenComposer.Compose(original,new Dictionary<string,XDocument>(),new JObject());
            var ordered=ClassicScreenPreflight.Items(result).Single().Element("ObjectList").Elements().Select(x=>x.Name.LocalName).ToArray();
            Assert(Array.IndexOf(ordered,"Hmi.Dynamic.VisibilityAnimation") < Array.IndexOf(ordered,"Hmi.Globalization.Font"));
            original.Root.Add(new XElement("Link",new XAttribute("TargetID","2")));
            Reject(()=>ClassicScreenComposer.Compose(original,new Dictionary<string,XDocument>(),new JObject()));
        });
        check("Graphic resize preflight requires an observed native size for the same picture list", () => {
            var original=Screen();var item=ClassicScreenPreflight.Items(original).Single();item.Name="Hmi.Screen.GraphicIOField";
            item.Add(new XElement("LinkList",new XElement("PictureList",new XElement("Name","Status"))));
            var candidate=new XDocument(original);ClassicScreenPreflight.Items(candidate).Single().Element("AttributeList").SetElementValue("Width",20);
            Reject(()=>ClassicScreenPreflight.Check(candidate,original,new[]{original},new HashSet<string>()));
            ClassicScreenPreflight.Check(original,original,new[]{original},new HashSet<string>());
        });
    }
}
