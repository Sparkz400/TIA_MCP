using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using TiaMcp.Model;

static class ClassicScreenAreaTests
{
    public static void Run(Action<string,Action> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("Area workflow assertion failed"); }
        check("Targeted tag roots preserve quoted names and never match sibling prefixes", () => {
            Assert(ClassicScreenPreflight.UsesRoot("\"DB.HMI\".Belt[0].Speed","DB.HMI"));
            Assert(!ClassicScreenPreflight.UsesRoot("\"DB.HMI2\".Belt[0].Speed","DB.HMI"));
            Assert(ClassicScreenPreflight.UsesRoot("Recipe.Real[0]","Recipe.Real"));
        });
        check("Parsed artifact reuse cannot leak caller edits into another composition", () => {
            var bytes=System.Text.Encoding.UTF8.GetBytes("<Document><Keep>original</Keep></Document>");
            var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var cache=new ClassicScreenDocumentCache(); var first=cache.Read(hash,bytes);
            first.Root.Element("Keep").Value="changed";
            Assert(cache.Read(hash,bytes).Root.Element("Keep").Value=="original");
            bytes[0]=(byte)'!';bool refused=false;try{cache.Read(hash,bytes);}catch(ArgumentException){refused=true;}Assert(refused);
        });
        check("Two realistic area screens compose 122 controls with preserved permissions and inert multilingual previews", () => {
            var root=new DirectoryInfo(AppContext.BaseDirectory);
            while(root!=null && !File.Exists(Path.Combine(root.FullName,"TiaMcp.slnx"))) root=root.Parent;
            if(root==null) throw new Exception("Repository fixture root missing");
            var source=XDocument.Load(Path.Combine(root.FullName,"fixtures/hmi/area-workflow/prototypes.xml"));
            var documents=new Dictionary<string,XDocument>{{"/hmi/screens/Prototypes",source}};
            var index=ClassicScreenAreaReport.Prototypes(documents);
            Assert(index["items"].Count()==4 && index.Value<bool>("complete"));
            Assert(index["items"][0]["interfaceValues"].Any(v=>v.Value<string>("name")=="Operability" && v.Value<bool>("linked")));
            foreach(int count in new[]{66,56})
            {
                var blank=new XDocument(source);blank.Descendants("Hmi.Screen.ScreenLayer").Remove();
                var components=new JArray();
                for(int i=0;i<count;i++) components.Add(new JObject { ["sourceScreenPath"]="/hmi/screens/Prototypes",["sourceItem"]="ManualSelector",["name"]="Control"+i,
                    ["attributes"]=new JObject { ["Left"]=245+i%4*275,["Top"]=10+i/4*35,["Width"]=260,["Height"]=34 },
                    ["interfaceValues"]=new JObject { ["FunctionID"]=1000+i },
                    ["captions"]=new JArray(new[]{"de-DE","en-GB","nl-NL"}.Select(c=>new JObject { ["compositionName"]="Text",["culture"]=c,["text"]="Control "+i })) });
                components[0]["captions"][0]["text"]="</script><img src=x onerror=alert(1)>"+new string('x',100);
                var request=new JObject { ["layers"]=new JArray(new JObject { ["sourceScreenPath"]="/hmi/screens/Prototypes",["sourceLayer"]=0 }),["components"]=components };
                var result=ClassicScreenComposer.Compose(blank,documents,request);
                var preflight=ClassicScreenPreflight.Check(result,blank,documents.Values,new HashSet<string>());
                Assert(preflight.Value<int>("itemCount")==count && result.Descendants("VendorExtension").Count()==count);
                Assert(result.Descendants("Tag").Elements("Name").Count(n=>n.Value=="Permissions.Manual")==count);
                var review=ClassicScreenPreview.Review(result,new[]{"de-DE","en-GB","nl-NL"});
                Assert(review["cultures"].Count()==3 && !review.Value<bool>("runtimeVerified"));
                var html=ClassicScreenPreview.Html(review);
                Assert(!html.Contains("</script><img") && html.Contains("connect-src 'none'") && review["warnings"].Any(w=>w.Value<string>("code")=="PossibleTextOverflow"));
                Assert(!review["warnings"].Any(w=>w.Value<string>("code")=="ReviewOverlap" || w.Value<string>("code")=="ReviewOperatingPermission"));
            }
        });
        check("Area declarations retain source paths and never claim a semantic mapping", () => {
            var report=ClassicScreenAreaReport.Declarations(XDocument.Parse("<Document><Section Name='Static'><Member Name='HMI' Datatype='AreaType'><Member Name='Ready' Datatype='Bool'/></Member></Section><Call><Parameter Name='ManualID'>123</Parameter></Call></Document>"));
            Assert(report["members"][1].Value<string>("path")=="HMI.Ready" && report["calls"].Count()==1 && !report.Value<bool>("semanticMappingsVerified"));
        });
        check("Native datatype links expose stale HMI members without guessed array expansion", () => {
            var table=XDocument.Parse("<Document><Hmi.Tag.Tag><AttributeList><Name>Area.HMI</Name></AttributeList><LinkList><DataType><Name>AreaType</Name></DataType><ControllerTag><Name>DB.HMI</Name></ControllerTag></LinkList><ObjectList><Hmi.Tag.TagStructureMember><AttributeList><Name>Old</Name></AttributeList></Hmi.Tag.TagStructureMember></ObjectList></Hmi.Tag.Tag></Document>");
            var type=XDocument.Parse("<Document><Member Name='Old' Datatype='Bool'/><Member Name='New' Datatype='Bool'/><Member Name='Belts' Datatype='Array[0..1] of Bool'/></Document>");
            var findings=ClassicScreenAreaReport.BindingFindings(new[]{table},new Dictionary<string,XDocument>{{"AreaType",type}},null);
            Assert(findings.Count==1 && findings[0].Value<string>("member")=="Area.HMI.New");
        });
    }
}
