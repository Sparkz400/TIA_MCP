using Newtonsoft.Json.Linq;
using Siemens.Engineering.SW.TechnologicalObjects;
using Siemens.Engineering.SW.TechnologicalObjects.Motion;
using TiaMcp.Openness.V21;
using WorkerError = TiaMcp.Openness.V21.BridgeException;

namespace TiaMcp.Tests
{
    internal static class TechnologyChildrenTests
    {
        private const string Root = "/devices/PLC/plc/CPU/technology";
        private static TechnologicalInstanceDB Item(string name) => new([]) { Name = name };
        private static TechnologicalInstanceDBGroup Group(params TechnologicalInstanceDB[] rows) => new() { TechnologicalObjects = rows };
        private static JObject Select(string path) => new() { ["technologyPath"] = path };
        private static async Task Refuse(string code, Action action)
        { var error = await Check.ThrowsAsync<WorkerError>(() => { action(); return Task.CompletedTask; }); Check.Equal(code,error.Code); }

        internal static Task OwnershipAndPaths()
        {
            var a = Item("Axis/%日本"); var cam = Item("Cam/😀"); var input = Item("Cam/😀");
            a.ChildContainer = new() { OutputCams = [cam], MeasuringInputs = [input] };
            var group = Group(a); var rows = OpennessSession.TechnologyEntriesForTest(group,Root,20);
            var entries = (JArray)rows["rows"]!;
            string owner = Root + "/Axis%2F%25%E6%97%A5%E6%9C%AC";
            Check.Equal(3,entries.Count); Check.Equal(false,(bool)rows["truncated"]!);
            Check.Equal(Root,(string?)entries[0]!["ownerPath"]); Check.Equal("TechnologicalObjects",(string?)entries[0]!["composition"]);
            Check.Equal(owner + "/children/OutputCams/Cam%2F%F0%9F%98%80",(string?)entries[1]!["path"]);
            Check.Equal(owner,(string?)entries[1]!["ownerPath"]); Check.Equal("OutputCams",(string?)entries[1]!["composition"]);
            Check.Equal(owner + "/children/MeasuringInputs/Cam%2F%F0%9F%98%80",(string?)entries[2]!["path"]);
            Check.Equal(owner,(string?)entries[2]!["ownerPath"]); Check.Equal("MeasuringInputs",(string?)entries[2]!["composition"]);
            Check.Equal(1,a.ServiceReads); Check.Equal(1,cam.ServiceReads); Check.Equal(1,input.ServiceReads);
            Check.Equal(0,a.Deletes + cam.Deletes + input.Deletes);
            return Task.CompletedTask;
        }

        internal static async Task OwnerBoundMutation()
        {
            var a = Item("A"); var b = Item("B"); var selected = Item("Cam"); var peer = Item("Cam");
            var children = new List<TechnologicalInstanceDB> { selected };
            a.ChildContainer = new() { OutputCams = children }; b.ChildContainer = new() { OutputCams = [peer] };
            var root = Group(a,b); string path = Root + "/A/children/OutputCams/Cam"; var request = Select(path);
            await Refuse("AmbiguousTarget",() => OpennessSession.FindTechnologyForTest(root,Root,new JObject { ["name"] = "Cam" },true));
            OpennessSession.BindTechnologyForTest(root,Root,request); Check.Equal("Cam",(string?)request["name"]);
            Check.Equal(path,OpennessSession.FindTechnologyForTest(root,Root,request,true));
            await Refuse("TechnologyObjectNotFound",() => OpennessSession.FindTechnologyForTest(root,Root,Select(Root + "/Cam"),true));
            selected.OnDelete = () => children.Remove(selected);
            OpennessSession.DeleteTechnologyForTest(root,Root,request);
            Check.Equal<string?>(null,OpennessSession.FindTechnologyForTest(root,Root,request,false));
            Check.Equal(1,selected.Deletes); Check.Equal(0,a.Deletes + b.Deletes + peer.Deletes);
            Check.Equal(Root + "/B/children/OutputCams/Cam",OpennessSession.FindTechnologyForTest(root,Root,new JObject { ["name"] = "Cam" },true));
            await Refuse("TechnologyObjectNotFound",() => OpennessSession.DeleteTechnologyForTest(root,Root,request));
            Check.Equal(1,selected.Deletes); Check.Equal(0,peer.Deletes);
        }

        internal static async Task IncompleteDiscovery()
        {
            foreach (string mode in new[] { "service", "firstCollection", "secondCollection", "nullCollection" })
            {
                var a = Item("A"); var root = Group(a); a.ChildContainer = new();
                if (mode == "service") a.Service = () => throw new InvalidOperationException("service lookup failed");
                if (mode == "firstCollection") a.ChildContainer.OutputCams = BrokenRows();
                if (mode == "secondCollection") a.ChildContainer.MeasuringInputs = BrokenRows();
                if (mode == "nullCollection") a.ChildContainer.OutputCams = null!;
                foreach (Action act in new Action[] {
                    () => OpennessSession.FindTechnologyForTest(root,Root,Select(Root + "/A"),true),
                    () => OpennessSession.FindTechnologyForTest(root,Root,Select(Root + "/absent"),false),
                    () => OpennessSession.TechnologyFingerprintForTest(root,Root),
                    () => OpennessSession.DeleteTechnologyForTest(root,Root,Select(Root + "/A")) })
                {
                    if (mode == "nullCollection") await Refuse("TraversalIncomplete",act);
                    else await Check.ThrowsAsync<InvalidOperationException>(() => { act(); return Task.CompletedTask; });
                }
                Check.Equal(0,a.Deletes);
            }
        }

        internal static async Task BoundsAndCycles()
        {
            var a = Item("A"); var child = Item("Cam"); var next = Item("Next");
            a.ChildContainer = new() { OutputCams = [child,next] }; var root = Group(a);
            var limited = OpennessSession.TechnologyEntriesForTest(root,Root,2);
            Check.Equal(2,((JArray)limited["rows"]!).Count); Check.Equal(true,(bool)limited["truncated"]!);
            Check.Equal(0,next.ServiceReads);
            a.ChildContainer.OutputCams = [a];
            var cycle = OpennessSession.TechnologyEntriesForTest(root,Root,1000);
            Check.Equal(65,((JArray)cycle["rows"]!).Count); Check.Equal(true,(bool)cycle["truncated"]!);
            await Refuse("TraversalLimit",() => OpennessSession.FindTechnologyForTest(root,Root,Select(Root + "/A"),true));
            await Refuse("TraversalLimit",() => OpennessSession.TechnologyFingerprintForTest(root,Root));
            var tail = a; a.ChildContainer = new();
            for (int i=0;i<64;i++) { var entry=Item("C"+i); tail.ChildContainer=new() { MeasuringInputs=[entry] }; tail=entry; }
            var boundary = OpennessSession.TechnologyEntriesForTest(root,Root,1000);
            Check.Equal(65,((JArray)boundary["rows"]!).Count); Check.Equal(false,(bool)boundary["truncated"]!);
            tail.ChildContainer=new() { OutputCams=[Item("TooDeep")] };
            Check.Equal(true,(bool)OpennessSession.TechnologyEntriesForTest(root,Root,1000)["truncated"]!);
        }

        internal static Task FingerprintIncludesChildren()
        {
            var a=Item("A");var b=Item("B");var cam=Item("Cam");var group=Group(a,b);
            a.ChildContainer=new() { OutputCams=[cam] };b.ChildContainer=new();
            var before=OpennessSession.TechnologyFingerprintForTest(group,Root);
            Check.Equal(3,before.Count);Check.Equal(Root+"/A",(string?)before[1]!["ownerPath"]);
            a.ChildContainer.OutputCams=[];b.ChildContainer.OutputCams=[cam];
            var after=OpennessSession.TechnologyFingerprintForTest(group,Root);
            Check.Equal(false,JToken.DeepEquals(before,after));
            Check.Equal(Root+"/B",(string?)after[2]!["ownerPath"]);
            Check.Equal("OutputCams",(string?)after[2]!["composition"]);
            return Task.CompletedTask;
        }

        private static IEnumerable<TechnologicalInstanceDB> BrokenRows()
        { yield return Item("Partial"); throw new InvalidOperationException("child enumeration failed"); }
    }
}
namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        internal static JObject TechnologyEntriesForTest(TechnologicalInstanceDBGroup group,string root,int maximum)
        {
            var budget=new ScanBudget(maximum);
            var rows=new JArray(TechnologyObjects(group,root,budget).Select(entry => new JObject {
                ["path"]=entry.Path,["ownerPath"]=entry.OwnerPath,["composition"]=entry.Composition }));
            return new JObject { ["rows"]=rows,["truncated"]=budget.Truncated,["visited"]=budget.Used };
        }
    }
}
